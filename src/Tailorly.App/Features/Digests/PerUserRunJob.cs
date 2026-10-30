using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tailorly.App.Ai;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Features.Accounts;
using Tailorly.App.Features.Admin;
using Tailorly.App.Features.Jobs;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Budgets;
using Tailorly.Core.Jobs;
using Tailorly.Core.Matching;
using Tailorly.Core.Time;

namespace Tailorly.App.Features.Digests;

/// <summary>
/// <c>PerUserRun</c> (PRD F7, SDD §7.3): one user's search, for a scheduled digest (claimed by the dispatcher) or a
/// manual "Run now". Steps: new jobs since the previous scheduled digest → raw-field prefilter → get-or-create shared
/// enrichment → enriched prefilter → <c>job_fit_score</c> for at most <c>Scoring:MaxPerRun</c> → digest items (salary
/// rule, minimum score, eligibility display rule, ranking) → <c>SendEmail</c> for scheduled digests.
/// <para>
/// Failure policy (SDD §11.1): a transient failure (AI provider unavailable, or anything unexpected) marks the run
/// <c>failed</c> and schedules a retry after 10/30/60 minutes while still inside the 4-hour window. When no retry is left,
/// an AI outage falls back to a rule-based digest (<c>degraded</c>) so the user is never silently skipped; a used-up
/// Discovery budget falls back immediately. Manual runs do not wait for retries. Idempotent: a ready run is left alone,
/// and jobs already scored for this résumé version are never scored again.
/// </para>
/// </summary>
public sealed partial class PerUserRunJob(
    AppDbContext db,
    EnrichmentService enrichment,
    ScoringService scoring,
    ScoredJobs scoredJobs,
    BudgetService budgets,
    IBackgroundJobClient jobs,
    IOptions<ScoringOptions> scoringOptions,
    IOptions<DigestOptions> options,
    TimeProvider clock,
    ILogger<PerUserRunJob> logger)
{
    /// <summary>Error codes a failed run carries while a retry is scheduled.</summary>
    public static readonly IReadOnlySet<string> RetryableCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        ErrorCodes.AiUnavailable, ErrorCodes.InternalError,
    };

    private sealed record Counts(int Considered, int Scored, bool Degraded);

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid digestId, Guid userId, CancellationToken ct)
    {
        using var _ = CurrentUser.ActAs(userId);
        var digest = await db.Digests.FirstOrDefaultAsync(d => d.Id == digestId, ct);
        if (digest is null || digest.Status == WorkStatus.Ready) return;
        if (digest.Status == WorkStatus.Failed)
        {
            if (digest.ErrorCode is null || !RetryableCodes.Contains(digest.ErrorCode)) return;
            digest.Attempt++;
        }

        var account = await db.UserProfiles.AsNoTracking().Select(p => p.AccountStatus).FirstOrDefaultAsync(ct);
        if (account != AccountStatus.Active)
        {
            await FailAsync(digest, ErrorCodes.AccountDisabled, ct);
            return;
        }
        var ctx = await MatchContext.LoadAsync(db, userId, ct);
        if (ctx is null)
        {
            await FailAsync(digest, ErrorCodes.ResumeRequired, ct);
            return;
        }

        var startedAt = clock.GetUtcNow();
        digest.Status = WorkStatus.Running;
        digest.StartedAt = startedAt;
        digest.FinishedAt = null;
        digest.ErrorCode = null;
        await db.SaveChangesAsync(ct);

        Counts counts;
        try
        {
            counts = await PipelineAsync(digest, ctx, rulesOnly: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var aiOutage = ex is AiTaskFailedException;
            var code = ex is AiTaskFailedException a ? a.Code : ErrorCodes.InternalError;
            if (await RetryDelayAsync(digest, ctx, startedAt, ct) is { } delay)
            {
                await FailAsync(digest, code, ct);
                jobs.Schedule<PerUserRunJob>(j => j.RunAsync(digestId, userId, CancellationToken.None), delay);
                LogRetry(logger, digestId, digest.Attempt, code, (int)delay.TotalMinutes, aiOutage ? null : ex.GetType().Name);
                return;
            }
            if (!aiOutage)
            {
                LogCrashed(logger, ex, digestId, ex.GetType().Name);
                await FailAsync(digest, code, ct);
                return;
            }
            // No retry left: deliver what rules can rank rather than nothing (PRD F7 "never fail silently").
            db.ChangeTracker.Clear();
            digest = await db.Digests.FirstAsync(d => d.Id == digestId, ct);
            counts = await PipelineAsync(digest, ctx, rulesOnly: true, ct);
        }

        digest.Status = WorkStatus.Ready;
        digest.ErrorCode = null;
        digest.FinishedAt = clock.GetUtcNow();
        digest.JobsConsidered = counts.Considered;
        digest.JobsScored = counts.Scored;
        digest.Degraded = counts.Degraded;
        await db.SaveChangesAsync(ct);

        if (digest.Kind == DigestKinds.Scheduled && await FeatureFlags.IsOnAsync(db, FeatureFlag.DigestEmailEnabled, ct))
            jobs.Enqueue<DigestEmailJob>(j => j.SendAsync(digestId, userId, CancellationToken.None));
        LogFinished(logger, digestId, digest.Kind, counts.Considered, counts.Scored, counts.Degraded);
    }

    private async Task<Counts> PipelineAsync(Digest digest, MatchContext ctx, bool rulesOnly, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var max = Math.Max(1, scoringOptions.Value.MaxPerRun);
        var since = await CandidatesSinceAsync(digest, now, ct);

        // 1. Candidates: new canonical listings from enabled sources not yet scored for this résumé version (DatabaseDesign §7).
        var enabled = await db.JobSources.AsNoTracking().Where(s => s.Enabled).Select(s => s.Key).ToListAsync(ct);
        var version = ctx.ResumeVersion;
        var candidates = await db.Jobs.AsNoTracking()
            .Where(j => j.DuplicateOfJobId == null && j.ClosedAt == null && (j.ExpiresAt == null || j.ExpiresAt > now)
                        && j.CreatedAt > since && enabled.Contains(j.SourceKey)
                        && !db.UserJobScores.Any(s => s.JobId == j.Id && s.ResumeVersion == version))
            .Select(j => new
            {
                j.Id, j.Title, j.CompanyName, j.LocationText, j.WorkMode, j.EmploymentType, j.SourceMeta, j.DescriptionCompleteness,
                j.PostedAt, j.CreatedAt, Head = j.DescriptionText == null ? null : j.DescriptionText.Substring(0, 500),
            })
            .ToListAsync(ct);

        // 2. Raw-field prefilter, most relevant (then newest) first.
        var kept = candidates
            .Select(c => (Job: c, Countries: CoverageReport.Countries(c.SourceMeta), Verdict: RawPrefilter.Apply(
                new RawCandidate(c.Id, c.Title, c.CompanyName, c.LocationText, c.WorkMode, c.EmploymentType,
                    CoverageReport.Countries(c.SourceMeta), c.Head), ctx.Match, ctx.Skills)))
            .Where(x => x.Verdict.Keep)
            .OrderByDescending(x => x.Verdict.Relevance).ThenByDescending(x => x.Job.PostedAt ?? x.Job.CreatedAt)
            .ToList();

        // 3. Enrichment on demand (shared, platform cost).
        var ceilingHit = false;
        if (!rulesOnly && kept.Count > 0)
            ceilingHit = (await enrichment.EnsureAsync(kept.Select(k => k.Job.Id).ToList(), ct)).CeilingHit;
        var keptIds = kept.Select(k => k.Job.Id).ToList();
        var enrichments = (await db.JobEnrichment.AsNoTracking().Where(e => keptIds.Contains(e.JobId))
                .Select(e => new { e.JobId, e.Data }).ToListAsync(ct))
            .Select(e => (e.JobId, Data: ScoredJobs.ParseEnrichment(e.Data)))
            .Where(e => e.Data is not null)
            .ToDictionary(e => e.JobId, e => e.Data!.Enrichment);

        // 4. Enriched prefilter (eligibility vs scope, work mode, job type, hiring model, India location), then the cap.
        var toScore = new List<ScoringJob>();
        var byRules = new List<(Guid JobId, double Relevance, EnrichmentOutput? Enrichment)>();
        foreach (var (job, countries, verdict) in kept)
        {
            if (toScore.Count + byRules.Count >= max) break;
            var indiaLocated = EligibilityRules.ListsIndia(countries) || EligibilityRules.NamesIndia(job.LocationText);
            if (enrichments.TryGetValue(job.Id, out var e))
            {
                var facts = new EnrichedFacts(e.Eligibility, e.WorkMode ?? job.WorkMode, e.EmploymentType ?? job.EmploymentType,
                    e.HiringModelHint, job.LocationText, indiaLocated);
                if (EnrichedPrefilter.Check(facts, ctx.Match) != EnrichedPrefilter.Kept) continue;
                if (rulesOnly) byRules.Add((job.Id, verdict.Relevance, e));
                else toScore.Add(new ScoringJob(job.Id, verdict.Relevance, new FitJobFacts(job.Title, job.CompanyName, job.LocationText,
                    job.WorkMode, job.EmploymentType, e.Eligibility, job.DescriptionCompleteness, e)));
            }
            else if (rulesOnly || ceilingHit)
            {
                // Not enriched (ceiling reached or AI unavailable): eligibility from the source fields and phrase rules.
                var hint = EligibilityRules.Classify(countries, job.LocationText, job.Title, job.Head);
                var facts = new EnrichedFacts(hint.Eligibility, job.WorkMode, job.EmploymentType, null, job.LocationText, indiaLocated);
                if (EnrichedPrefilter.Check(facts, ctx.Match) != EnrichedPrefilter.Kept) continue;
                byRules.Add((job.Id, verdict.Relevance, null));
            }
            // else: not enriched this run (over Enrichment:MaxPerRun or the reply failed); a later run may pick it up.
        }

        // 5. Scoring (Discovery budget); whatever the model cannot score is ranked by rules.
        var scored = 0;
        var degraded = rulesOnly || (ceilingHit && byRules.Count > 0);
        var fallbackReason = rulesOnly ? FallbackReasons.AiUnavailable : FallbackReasons.NotEnriched;
        if (toScore.Count > 0)
        {
            if ((await budgets.GetAsync(ctx.UserId, BudgetBuckets.Discovery, ct)).IsExhausted)
            {
                scored += await scoring.StoreRuleScoresAsync(ctx, toScore.Select(s => (s.JobId, s.Relevance, (EnrichmentOutput?)s.Facts.Enrichment)),
                    FallbackReasons.BudgetExhausted, ct);
                degraded = true;
            }
            else
            {
                var outcome = await scoring.ScoreAsync(ctx, toScore, ct);
                scored += outcome.Scored;
                if (outcome.NotScored.Count > 0)
                {
                    scored += await scoring.StoreRuleScoresAsync(ctx,
                        outcome.NotScored.Select(s => (s.JobId, s.Relevance, (EnrichmentOutput?)s.Facts.Enrichment)),
                        FallbackReasons.BudgetExhausted, ct);
                    degraded = true;
                }
            }
        }
        if (byRules.Count > 0) scored += await scoring.StoreRuleScoresAsync(ctx, byRules, fallbackReason, ct);

        // 6. Digest items.
        await AssembleAsync(digest, ctx, now, ct);
        return new Counts(candidates.Count, scored, degraded);
    }

    /// <summary>
    /// The digest's items: every job scored since the previous scheduled digest finished (this run's scores and any
    /// "Run now" results in between), one per job, that is open and passes the eligibility display rule, the salary rule
    /// and the minimum score, ranked by fit then recency. Rewritten on a retry.
    /// </summary>
    private async Task AssembleAsync(Digest digest, MatchContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        var since = await ItemsSinceAsync(digest, now, ct);
        var rows = await scoredJobs.Query(db.UserJobScores.Where(s => s.CreatedAt > since)).ToListAsync(ct);
        var views = ScoredJobs.LatestPerJob(await scoredJobs.EvaluateAsync(rows, ctx.Match, ct))
            .Where(v => v.VisibleInApp && v.MeetsMinScore(ctx.Match.MinMatchScore))
            .ToList();
        var ranked = ScoredJobs.Rank(views);

        await db.DigestItems.Where(i => i.DigestId == digest.Id).ExecuteDeleteAsync(ct);
        short rank = 0;
        foreach (var v in ranked.Take(short.MaxValue))
            db.DigestItems.Add(new DigestItem { DigestId = digest.Id, ScoreId = v.Row.ScoreId, UserId = ctx.UserId, Rank = ++rank });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>New jobs are those first seen after the previous scheduled digest started, bounded to the lookback (72 h).</summary>
    private async Task<DateTimeOffset> CandidatesSinceAsync(Digest digest, DateTimeOffset now, CancellationToken ct)
    {
        var floor = now.AddHours(-Math.Max(1, options.Value.LookbackHours));
        var previous = await db.Digests.AsNoTracking()
            .Where(d => d.Kind == DigestKinds.Scheduled && d.Id != digest.Id && d.CreatedAt < digest.CreatedAt && d.StartedAt != null)
            .MaxAsync(d => d.StartedAt, ct);
        return previous is { } p && p > floor ? p : floor;
    }

    /// <summary>Items are scores made after the previous scheduled digest finished, bounded to the lookback.</summary>
    private async Task<DateTimeOffset> ItemsSinceAsync(Digest digest, DateTimeOffset now, CancellationToken ct)
    {
        var floor = now.AddHours(-Math.Max(1, options.Value.LookbackHours));
        var previous = await db.Digests.AsNoTracking()
            .Where(d => d.Kind == DigestKinds.Scheduled && d.Id != digest.Id && d.CreatedAt < digest.CreatedAt
                        && d.Status == WorkStatus.Ready)
            .MaxAsync(d => d.FinishedAt, ct);
        return previous is { } p && p > floor ? p : floor;
    }

    /// <summary>
    /// The delay before the next attempt, or null when no retry is left: manual runs never wait; scheduled runs retry
    /// (10/30/60 min) only while the retry would still start inside the window (schedule time + 4 h, user's time zone).
    /// </summary>
    private async Task<TimeSpan?> RetryDelayAsync(Digest digest, MatchContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        var delays = options.Value.RetryDelays;
        if (digest.Kind != DigestKinds.Scheduled || digest.Attempt > delays.Count) return null;
        var delay = TimeSpan.FromMinutes(delays[digest.Attempt - 1]);
        var zone = await db.UserProfiles.AsNoTracking().Select(p => p.TimeZone).FirstOrDefaultAsync(ct);
        var windowEnd = WindowEnd(digest.LocalDate, ctx.Preferences.ScheduleLocalTime, zone, options.Value.WindowHours)
                        ?? digest.CreatedAt.AddHours(options.Value.WindowHours);
        return now + delay < windowEnd ? delay : null;
    }

    /// <summary>The end of a scheduled digest's window as UTC, or null if the zone or local time cannot be resolved.</summary>
    internal static DateTimeOffset? WindowEnd(DateOnly localDate, TimeOnly scheduleTime, string? zone, int windowHours)
    {
        if (zone is null || !TimeZoneInfo.TryFindSystemTimeZoneById(TimeZones.Canonicalize(zone), out var tz)) return null;
        var local = localDate.ToDateTime(scheduleTime, DateTimeKind.Unspecified);
        // A schedule time skipped by a DST change starts the window at the first valid local time after it.
        while (tz.IsInvalidTime(local)) local = local.AddMinutes(30);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime().AddHours(windowHours);
    }

    private async Task FailAsync(Digest digest, string code, CancellationToken ct)
    {
        digest.Status = WorkStatus.Failed;
        digest.ErrorCode = code;
        digest.FinishedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        LogFailed(logger, digest.Id, code);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Digest {DigestId} ({Kind}) ready: {Considered} considered, {Scored} scored, degraded {Degraded}")]
    private static partial void LogFinished(ILogger logger, Guid digestId, string kind, int considered, int scored, bool degraded);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Digest {DigestId} attempt {Attempt} failed with {ErrorCode}; retry in {DelayMinutes} min ({ExceptionType})")]
    private static partial void LogRetry(ILogger logger, Guid digestId, int attempt, string errorCode, int delayMinutes, string? exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Digest {DigestId} failed with {ErrorCode}")]
    private static partial void LogFailed(ILogger logger, Guid digestId, string errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Digest {DigestId} run crashed ({ExceptionType})")]
    private static partial void LogCrashed(ILogger logger, Exception ex, Guid digestId, string exceptionType);
}
