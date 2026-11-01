using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Ai;
using Tailorly.App.Api;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Budgets;

namespace Tailorly.App.Features.Usage;

public sealed record UsageBucketDto(string Bucket, long Limit, long Used, long Remaining, int PercentUsed);

/// <summary>One AI task's calls and counted tokens this period (failed calls included: their tokens were used).</summary>
public sealed record UsageTaskDto(string Task, string Bucket, int Calls, long Tokens);

/// <param name="Date">A day in Asia/Kolkata (the budget calendar), <c>YYYY-MM-DD</c>.</param>
public sealed record UsageDayDto(string Date, long Tokens);

/// <param name="ResetsOn">First day of the next period (Asia/Kolkata), <c>YYYY-MM-DD</c>.</param>
/// <param name="Warnings"><c>{bucket}_80_percent</c> or <c>{bucket}_100_percent</c>.</param>
/// <param name="ByTask">This period's calls and tokens per task, most tokens first.</param>
/// <param name="Daily">Counted tokens per day for the last 30 days (oldest first, days without calls as 0).</param>
public sealed record UsageResponse(string PeriodKey, string ResetsOn, IReadOnlyList<UsageBucketDto> Buckets, IReadOnlyList<string> Warnings,
    IReadOnlyList<UsageTaskDto> ByTask, IReadOnlyList<UsageDayDto> Daily);

/// <summary>
/// <c>GET /usage</c> (ApiDesign §4.9): this month's Discovery and Documents budgets, plus the per-task breakdown and the
/// last 30 days from <c>ai_usage</c> for the Usage page. Only the caller's own rows; platform enrichment is not counted.
/// </summary>
public static class UsageEndpoints
{
    public const int Days = 30;
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    public static void MapUsageEndpoints(this RouteGroupBuilder api) =>
        api.MapGet("/usage", GetUsage).WithName("GetUsage").WithTags("Usage").RequireAuthorization(AuthPolicies.User);

    private static async Task<Ok<UsageResponse>> GetUsage(BudgetService budgets, AppDbContext db, ICurrentUser current, TimeProvider clock,
        CancellationToken ct)
    {
        var buckets = await budgets.GetAllAsync(current.UserId, ct);
        var period = budgets.CurrentPeriod;
        var userId = current.UserId;
        var byTask = await db.AiUsage.AsNoTracking()
            .Where(u => u.UserId == userId && u.PeriodKey == period && u.Bucket != BudgetBuckets.Platform)
            .GroupBy(u => new { u.Task, u.Bucket })
            .Select(g => new UsageTaskDto(g.Key.Task, g.Key.Bucket, g.Count(), g.Sum(u => (long)u.CountedTokens)))
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), India).DateTime);
        var first = today.AddDays(-(Days - 1));
        var since = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), India.GetUtcOffset(first.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var calls = await db.AiUsage.AsNoTracking()
            .Where(u => u.UserId == userId && u.CreatedAt >= since && u.Bucket != BudgetBuckets.Platform)
            .Select(u => new { u.CreatedAt, u.CountedTokens })
            .ToListAsync(ct);
        var perDay = calls.GroupBy(c => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(c.CreatedAt, India).DateTime))
            .ToDictionary(g => g.Key, g => g.Sum(c => (long)c.CountedTokens));
        var daily = Enumerable.Range(0, Days).Select(i => first.AddDays(i))
            .Select(d => new UsageDayDto(d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), perDay.GetValueOrDefault(d)))
            .ToList();

        return TypedResults.Ok(new UsageResponse(
            period,
            BudgetPolicy.FormatResetsOn(budgets.ResetsOn),
            buckets.Select(b => new UsageBucketDto(b.Bucket, b.Limit, b.Used, b.Remaining, b.PercentUsed)).ToList(),
            BudgetPolicy.Warnings(buckets),
            byTask.OrderByDescending(t => t.Tokens).ThenBy(t => t.Task, StringComparer.Ordinal).ToList(),
            daily));
    }
}
