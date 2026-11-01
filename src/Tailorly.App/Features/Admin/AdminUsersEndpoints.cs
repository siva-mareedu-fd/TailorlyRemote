using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Ai;
using Tailorly.App.Api;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Budgets;

namespace Tailorly.App.Features.Admin;

/// <summary>
/// User list, suspension and AI usage (ApiDesign §4.11; PRD F15 "per-user usage"). Ids, flags and counts only: no names,
/// no emails, no résumé or job content (the M6 brief: "user list (ids and counts only)"). An admin finds one account by
/// its exact email or id; the email is matched, never returned.
/// </summary>
public static partial class AdminUsersEndpoints
{
    public static void MapAdminUsers(this RouteGroupBuilder admin)
    {
        admin.MapGet("/users", ListUsers).WithName("AdminListUsers");
        admin.MapPost("/users/{id:guid}/suspend", Suspend).WithName("AdminSuspendUser");
        admin.MapPost("/users/{id:guid}/unsuspend", Unsuspend).WithName("AdminUnsuspendUser");
        admin.MapGet("/usage", Usage).WithName("AdminUsage");
    }

    /// <param name="q">An exact email address or a user id; anything else finds nothing.</param>
    private static async Task<Ok<AdminUserPage>> ListUsers(SystemQueries system, BudgetService budgets, UserManager<AppUser> users,
        string? q, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        if (status is not null && status is not (AccountStatus.Active or AccountStatus.Suspended or AccountStatus.PendingDeletion))
            throw FieldErrors.Single("status", "Choose active, suspended or pending_deletion.");
        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 20, 1, 50);
        Guid? id = null;
        string? email = null;
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            if (Guid.TryParse(term, out var g)) id = g;
            else if (term.Length <= 254) email = users.NormalizeEmail(term);
            else return TypedResults.Ok(new AdminUserPage([], p, size, 0, budgets.CurrentPeriod));
        }
        var (rows, total) = await system.AdminUsersAsync(status, id, email, budgets.CurrentPeriod, p, size, ct);
        return TypedResults.Ok(new AdminUserPage(rows, p, size, total, budgets.CurrentPeriod));
    }

    /// <summary>
    /// Suspends an active account: every authenticated call answers <c>403 ACCOUNT_DISABLED</c> from the next request,
    /// sign-in is refused, and the digest dispatcher skips it. The security stamp rotates so other sessions end too.
    /// </summary>
    private static Task<Ok<AdminUserRow>> Suspend(Guid id, HttpContext http, SystemQueries system, UserManager<AppUser> users,
        BudgetService budgets, ICurrentUser current, CancellationToken ct) =>
        ChangeStatusAsync(id, AccountStatus.Active, AccountStatus.Suspended, http, system, users, budgets, current, ct);

    private static Task<Ok<AdminUserRow>> Unsuspend(Guid id, HttpContext http, SystemQueries system, UserManager<AppUser> users,
        BudgetService budgets, ICurrentUser current, CancellationToken ct) =>
        ChangeStatusAsync(id, AccountStatus.Suspended, AccountStatus.Active, http, system, users, budgets, current, ct);

    private static async Task<Ok<AdminUserRow>> ChangeStatusAsync(Guid id, string from, string to, HttpContext http,
        SystemQueries system, UserManager<AppUser> users, BudgetService budgets, ICurrentUser current, CancellationToken ct)
    {
        if (id == current.UserId) throw FieldErrors.Single("id", "You cannot suspend your own account.");
        var row = await OneAsync(system, budgets, id, ct);
        if (row.Status != to)
        {
            if (row.Status != from)
                throw FieldErrors.Single("id", row.Status == AccountStatus.PendingDeletion
                    ? "This account is being deleted."
                    : $"This account is {row.Status.Replace('_', ' ')}.");
            await system.SetAccountStatusAsync(id, from, to, ct);
            if (to == AccountStatus.Suspended && await users.FindByIdAsync(id.ToString()) is { } user)
                await users.UpdateSecurityStampAsync(user);
            row = await OneAsync(system, budgets, id, ct);
        }
        http.AuditDetails(new { from, to });
        return TypedResults.Ok(row);
    }

    private static async Task<AdminUserRow> OneAsync(SystemQueries system, BudgetService budgets, Guid id, CancellationToken ct) =>
        (await system.AdminUsersAsync(null, id, null, budgets.CurrentPeriod, 1, 1, ct)).Rows.SingleOrDefault()
        ?? throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Not found", "This user does not exist.");

    /// <summary>Totals per task and model, and per user (ids only), for one <c>YYYY-MM</c> period (default: this one).</summary>
    private static async Task<Ok<AdminUsageDto>> Usage(AppDbContext db, BudgetService budgets, string? period, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(period) ? budgets.CurrentPeriod : period.Trim();
        if (!PeriodFormat().IsMatch(key)) throw FieldErrors.Single("period", "Use a month such as 2026-11.");

        var rows = db.AiUsage.AsNoTracking().Where(u => u.PeriodKey == key);
        // Anonymous projections, so PostgreSQL can order and limit them (EF cannot order by a record's constructor arguments).
        var byTask = (await rows.GroupBy(u => new { u.Task, u.Model, u.Bucket })
                .Select(g => new
                {
                    g.Key.Task, g.Key.Model, g.Key.Bucket, Calls = g.Count(), Errors = g.Count(x => !x.Success),
                    Input = g.Sum(x => (long)x.InputTokens), Output = g.Sum(x => (long)x.OutputTokens),
                    Counted = g.Sum(x => (long)x.CountedTokens), Cost = g.Sum(x => x.EstCostUsd ?? 0),
                })
                .OrderBy(t => t.Task).ThenBy(t => t.Model).ThenBy(t => t.Bucket)
                .ToListAsync(ct))
            .Select(t => new AdminUsageTaskDto(t.Task, t.Model, t.Bucket, t.Calls, t.Errors, t.Input, t.Output, t.Counted, t.Cost))
            .ToList();
        var byUser = (await rows.Where(u => u.UserId != null)
                .GroupBy(u => u.UserId!.Value)
                .Select(g => new
                {
                    UserId = g.Key, Calls = g.Count(),
                    Discovery = g.Where(x => x.Bucket == BudgetBuckets.Discovery).Sum(x => (long)x.CountedTokens),
                    Documents = g.Where(x => x.Bucket == BudgetBuckets.Documents).Sum(x => (long)x.CountedTokens),
                })
                .OrderByDescending(u => u.Discovery + u.Documents).ThenBy(u => u.UserId)
                .Take(200)
                .ToListAsync(ct))
            .Select(u => new AdminUsageUserDto(u.UserId, u.Calls, u.Discovery, u.Documents))
            .ToList();
        var platform = byTask.Where(t => t.Bucket == BudgetBuckets.Platform).Sum(t => t.CountedTokens);
        return TypedResults.Ok(new AdminUsageDto(key, byTask, byUser, platform));
    }

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$")]
    private static partial Regex PeriodFormat();
}

/// <param name="Period">The budget period the token counts are for (<c>YYYY-MM</c>, Asia/Kolkata).</param>
public sealed record AdminUserPage(IReadOnlyList<AdminUserRow> Items, int Page, int PageSize, int Total, string Period);

public sealed record AdminUsageTaskDto(
    string Task,
    string Model,
    string Bucket,
    int Calls,
    int Errors,
    long InputTokens,
    long OutputTokens,
    long CountedTokens,
    decimal EstCostUsd);

public sealed record AdminUsageUserDto(Guid UserId, int Calls, long DiscoveryTokens, long DocumentsTokens);

/// <param name="ByUser">The 200 heaviest users this period (ids only).</param>
public sealed record AdminUsageDto(
    string Period,
    IReadOnlyList<AdminUsageTaskDto> ByTask,
    IReadOnlyList<AdminUsageUserDto> ByUser,
    long PlatformTokens);
