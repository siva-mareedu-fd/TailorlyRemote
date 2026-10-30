using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Tailorly.App.Ai;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Matching;

namespace Tailorly.App.Features.Digests;

/// <summary>A job on the scoring shortlist, with its relevance from the raw prefilter.</summary>
public sealed record ScoringJob(Guid JobId, double Relevance, FitJobFacts Facts);

/// <param name="Scored">AI scores stored.</param>
/// <param name="Failed">Jobs whose reply failed validation twice (skipped this run).</param>
/// <param name="BudgetExhausted">The Discovery budget ran out; <see cref="NotScored"/> should be ranked by rules.</param>
public sealed record ScoringOutcome(int Scored, int Failed, bool BudgetExhausted, IReadOnlyList<ScoringJob> NotScored);

/// <summary>Why a run ranked jobs by rules instead of the model (stored in the rule-ranked score's reasons).</summary>
public static class FallbackReasons
{
    public const string BudgetExhausted = "rule_based:discovery_budget_used_up";
    public const string AiUnavailable = "rule_based:ai_unavailable";
    public const string NotEnriched = "rule_based:not_enriched";
}

/// <summary>
/// Per-user <c>job_fit_score</c> (PRD §7.5 step 2): the cached candidate summary plus each job's enrichment JSON (not
/// the raw description), up to <c>Scoring:Parallelism</c> calls at a time, charged to the user's Discovery budget.
/// Scores are stored as they arrive and never re-scored for the same résumé version (a second insert is ignored).
/// </summary>
public sealed partial class ScoringService(IServiceScopeFactory scopes, IOptions<ScoringOptions> options, TimeProvider clock,
    ILogger<ScoringService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <exception cref="AiTaskFailedException">A transient provider failure; scores stored so far are kept.</exception>
    public async Task<ScoringOutcome> ScoreAsync(MatchContext ctx, IReadOnlyList<ScoringJob> jobs, CancellationToken ct)
    {
        if (jobs.Count == 0) return new(0, 0, false, []);
        var p = ctx.Preferences;
        var summary = JobFitScoreTask.CandidateSummary(ctx.Profile, ctx.ConfirmedSkills,
            new FitPreferences(p.TargetRoles, p.ExperienceLevel, p.TimezoneOverlap));

        int scored = 0, failed = 0, exhausted = 0;
        var notScored = new System.Collections.Concurrent.ConcurrentBag<ScoringJob>();
        AiTaskFailedException? transient = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(jobs,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.Parallelism), CancellationToken = stop.Token },
                async (job, token) =>
                {
                    if (Volatile.Read(ref exhausted) == 1)
                    {
                        notScored.Add(job);
                        return;
                    }
                    await using var scope = scopes.CreateAsyncScope();
                    var runner = scope.ServiceProvider.GetRequiredService<IAiTaskRunner>();
                    AiTaskResult<FitScoreOutput> result;
                    try
                    {
                        result = await runner.RunAsync(JobFitScoreTask.Definition, JobFitScoreTask.BuildInput(summary, job.Facts),
                            ctx.UserId, token);
                    }
                    catch (BudgetExceededException)
                    {
                        Volatile.Write(ref exhausted, 1);
                        notScored.Add(job);
                        return;
                    }
                    catch (AiTaskFailedException ex) when (!ex.Transient)
                    {
                        Interlocked.Increment(ref failed);
                        LogFailed(logger, job.JobId, ex.Code);
                        return;
                    }
                    catch (AiTaskFailedException ex)
                    {
                        Interlocked.CompareExchange(ref transient, ex, null);
                        await stop.CancelAsync();
                        return;
                    }

                    var f = result.Value;
                    var score = new UserJobScore
                    {
                        UserId = ctx.UserId,
                        JobId = job.JobId,
                        ResumeVersion = ctx.ResumeVersion,
                        FitScore = (short)Math.Clamp(f.FitScore, 1, 10),
                        Reasons = JsonSerializer.Serialize(f.Reasons, Json),
                        MatchedSkills = JsonSerializer.Serialize(f.MatchedSkills, Json),
                        MissingSkills = JsonSerializer.Serialize(f.MissingSkills, Json),
                        Confidence = JobFitScoreTask.CapConfidence(f.Confidence, job.Facts.DescriptionCompleteness),
                        Model = result.Model,
                        PromptVersion = result.PromptVersion,
                        CreatedAt = clock.GetUtcNow(),
                    };
                    if (await InsertAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), score)) Interlocked.Increment(ref scored);
                });
        }
        catch (OperationCanceledException) when (transient is not null && !ct.IsCancellationRequested)
        {
            // stopped because the provider is unavailable; reported below
        }

        if (transient is not null)
        {
            LogInterrupted(logger, ctx.UserId, scored);
            throw transient;
        }
        LogScored(logger, ctx.UserId, scored, failed, notScored.Count);
        return new(scored, failed, exhausted == 1, notScored.ToList());
    }

    /// <summary>
    /// Stores rule-ranked scores (model <c>rules</c>) for jobs the model could not score (PRD F7 fallback). Used when the
    /// Discovery budget is used up, the AI stayed unavailable until the end of the window, or enrichment hit the ceiling.
    /// </summary>
    public async Task<int> StoreRuleScoresAsync(MatchContext ctx, IEnumerable<(Guid JobId, double Relevance, EnrichmentOutput? Enrichment)> jobs,
        string reason, CancellationToken ct)
    {
        var stored = 0;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (jobId, relevance, e) in jobs)
        {
            var required = e?.SkillsRequired ?? [];
            var matched = required.Where(s => TermMatcher.SkillHits(s, ctx.Skills, allowShort: true) > 0).ToList();
            var score = new UserJobScore
            {
                UserId = ctx.UserId,
                JobId = jobId,
                ResumeVersion = ctx.ResumeVersion,
                FitScore = (short)Ranking.RuleScore(relevance, matched.Count, required.Count),
                Reasons = JsonSerializer.Serialize(new[] { reason }, Json),
                MatchedSkills = JsonSerializer.Serialize(matched, Json),
                MissingSkills = JsonSerializer.Serialize(required.Except(matched, StringComparer.OrdinalIgnoreCase).ToList(), Json),
                Confidence = "low",
                Model = UserJobScore.RulesModel,
                CreatedAt = clock.GetUtcNow(),
            };
            if (await InsertAsync(db, score)) stored++;
        }
        return stored;
    }

    /// <summary>Adds one score; an existing score for the same (user, job, résumé version) wins. A deleted job is skipped.</summary>
    private static async Task<bool> InsertAsync(AppDbContext db, UserJobScore score)
    {
        db.UserJobScores.Add(score);
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation,
                                           })
        {
            db.Entry(score).State = EntityState.Detached;
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Scoring for user {UserId}: {Scored} scored, {Failed} failed, {NotScored} left for rules")]
    private static partial void LogScored(ILogger logger, Guid userId, int scored, int failed, int notScored);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scoring of job {JobId} failed with {ErrorCode}")]
    private static partial void LogFailed(ILogger logger, Guid jobId, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scoring for user {UserId} interrupted: AI provider unavailable after {Scored} scored")]
    private static partial void LogInterrupted(ILogger logger, Guid userId, int scored);
}
