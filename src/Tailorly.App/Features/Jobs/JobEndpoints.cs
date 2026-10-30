using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Api;
using Tailorly.App.Features.Digests;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Jobs;
using Tailorly.Core.Matching;

namespace Tailorly.App.Features.Jobs;

/// <summary>
/// <c>GET /jobs</c> and <c>GET /jobs/{id}</c> (ApiDesign §4.6). Only jobs already scored for the caller are ever returned
/// (never the shared pool, API10), with the salary rule and eligibility display rule applied with the caller's current
/// preferences. Every job carries its source attribution and the source-supplied apply URL.
/// </summary>
public static class JobEndpoints
{
    public const int MaxPageSize = 50;
    private static readonly string[] Eligibilities =
        [Eligibility.Worldwide, Eligibility.IndiaEligible, Eligibility.Restricted, Eligibility.Unclear];
    private static readonly string[] SalaryFilters = [SalaryLabels.Stated, SalaryLabels.Estimated, SalaryLabels.NotListed];

    public static void MapJobEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/jobs").WithTags("Jobs").RequireAuthorization(AuthPolicies.User);
        g.MapGet("", List).WithName("ListJobs");
        g.MapGet("/{id:guid}", Get).WithName("GetJob");
    }

    private static ApiException NotFound() =>
        new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Not found", "This job is not available.");

    /// <param name="minScore">1–10; defaults to the caller's minimum match score. Jobs ranked by rules (no AI score) are not filtered by it.</param>
    /// <param name="eligibility">Repeatable: worldwide | india_eligible | restricted | unclear.</param>
    /// <param name="workMode">Repeatable: remote | hybrid | onsite.</param>
    /// <param name="salary">stated | estimated | not_listed.</param>
    /// <param name="q">Text in the title or company name.</param>
    /// <param name="savedOnly">Only jobs on the tracker (the tracker arrives in M5, so this returns no jobs yet).</param>
    /// <param name="sort"><c>-score</c> (default) or <c>-posted</c>.</param>
    private static async Task<Ok<JobPage>> List(AppDbContext db, ScoredJobs scored, ICurrentUser current, Guid? digestId, int? minScore,
        [FromQuery] string[]? eligibility, [FromQuery] string[]? workMode, string? salary, string? q, bool? savedOnly, string? sort,
        int? page, int? pageSize, CancellationToken ct)
    {
        var errors = new FieldErrors();
        if (minScore is < 1 or > 10) errors.Add("minScore", "Choose a score from 1 to 10.");
        if (eligibility?.Any(e => !Eligibilities.Contains(e, StringComparer.Ordinal)) == true)
            errors.Add("eligibility", $"Allowed values: {string.Join(", ", Eligibilities)}.");
        if (workMode?.Any(m => !WorkModes.All.Contains(m, StringComparer.Ordinal)) == true)
            errors.Add("workMode", $"Allowed values: {string.Join(", ", WorkModes.All)}.");
        if (salary is not null && !SalaryFilters.Contains(salary, StringComparer.Ordinal))
            errors.Add("salary", $"Allowed values: {string.Join(", ", SalaryFilters)}.");
        if (sort is not (null or "-score" or "-posted")) errors.Add("sort", "Use -score or -posted.");
        if (q is { Length: > 100 }) errors.Add("q", "Search text must be 100 characters or fewer.");
        errors.ThrowIfAny();

        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 20, 1, MaxPageSize);
        if (savedOnly == true) return TypedResults.Ok(new JobPage([], p, size, 0)); // tracker (user_jobs) is M5

        var prefs = await MatchContext.PreferencesAsync(db, current.UserId, ct);
        var match = MatchContext.ToMatch(prefs);
        IQueryable<UserJobScore> scores = db.UserJobScores;
        if (digestId is { } did)
        {
            if (!await db.Digests.AnyAsync(d => d.Id == did, ct)) throw NotFound();
            scores = scores.Where(s => db.DigestItems.Any(i => i.DigestId == did && i.ScoreId == s.Id));
        }
        var rows = await scored.Query(scores).ToListAsync(ct);
        var min = minScore ?? match.MinMatchScore;
        var text = q?.Trim();
        var views = ScoredJobs.LatestPerJob(await scored.EvaluateAsync(rows, match, ct))
            .Where(v => v.VisibleInApp && v.MeetsMinScore(min))
            .Where(v => eligibility is not { Length: > 0 } || eligibility.Contains(v.Eligibility, StringComparer.Ordinal))
            .Where(v => workMode is not { Length: > 0 } || (v.WorkMode is { } wm && workMode.Contains(wm, StringComparer.Ordinal)))
            .Where(v => salary is null || v.SalaryDecision.Label == salary)
            .Where(v => string.IsNullOrEmpty(text) || v.Row.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || v.Row.CompanyName.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ranked = ScoredJobs.Rank(views, byPosted: sort == "-posted");
        var items = await scored.ToSummariesAsync(ranked.Skip((p - 1) * size).Take(size).ToList(), ct);
        return TypedResults.Ok(new JobPage(items, p, size, ranked.Count));
    }

    /// <summary>
    /// Visible only if scored for the caller (or, from M5, tracked by them); 404 otherwise, or once the job was purged
    /// or is past its source's expiry (ApiDesign §4.6, §7).
    /// </summary>
    private static async Task<Ok<JobDetailDto>> Get(Guid id, AppDbContext db, ScoredJobs scored, ICurrentUser current, TimeProvider clock,
        CancellationToken ct)
    {
        var rows = await scored.Query(db.UserJobScores.Where(s => s.JobId == id), withDescription: true).ToListAsync(ct);
        if (rows.Count == 0) throw NotFound();
        var prefs = await MatchContext.PreferencesAsync(db, current.UserId, ct);
        var view = ScoredJobs.LatestPerJob(await scored.EvaluateAsync(rows, MatchContext.ToMatch(prefs), ct)).Single();
        if (view.Row.ExpiresAt is { } expires && expires <= clock.GetUtcNow()) throw NotFound();
        return TypedResults.Ok(await scored.ToDetailAsync(view, ct));
    }
}
