using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Tailorly.App.Ai;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Features.Admin;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Jobs;
using Tailorly.Core.Matching;
using Tailorly.Core.Time;

namespace Tailorly.App.Features.Digests;

/// <param name="CeilingHit">The platform daily ceiling stopped enrichment; the run ranks the rest by rules.</param>
public sealed record EnrichmentOutcome(int Created, int Failed, bool CeilingHit);

/// <summary>
/// Shared job enrichment as a get-or-create step inside the per-user run (SDD §8.5, ADR-006): for candidate jobs without
/// a <c>job_enrichment</c> row (best raw relevance first, at most <c>Enrichment:MaxPerRun</c>), call <c>job_enrichment</c>
/// as a platform cost (no user is charged) and store the result for every user. Stops at the platform daily token
/// ceiling. A concurrent run enriching the same job is harmless: the second insert is ignored. Eligibility is resolved
/// with the hard rules (SDD §8.6) before it is stored. Logs ids and counts only.
/// </summary>
public sealed partial class EnrichmentService(
    AppDbContext db,
    IServiceScopeFactory scopes,
    SystemQueries system,
    IOptions<EnrichmentOptions> options,
    IOptions<AiOptions> ai,
    TimeProvider clock,
    ILogger<EnrichmentService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="jobIdsByRelevance">Kept candidates, most relevant first.</param>
    /// <exception cref="AiTaskFailedException">A transient provider failure; the run should retry later.</exception>
    public async Task<EnrichmentOutcome> EnsureAsync(IReadOnlyList<Guid> jobIdsByRelevance, CancellationToken ct)
    {
        if (jobIdsByRelevance.Count == 0) return new(0, 0, false);
        var existing = await db.JobEnrichment.AsNoTracking().Where(e => jobIdsByRelevance.Contains(e.JobId))
            .Select(e => e.JobId).ToListAsync(ct);
        var have = existing.ToHashSet();
        var missing = jobIdsByRelevance.Where(id => !have.Contains(id)).Take(Math.Max(0, options.Value.MaxPerRun)).ToList();
        if (missing.Count == 0) return new(0, 0, false);

        var ceiling = ai.Value.PlatformDailyEnrichmentTokenCeiling;
        var spentBefore = ceiling is null ? 0 : await system.PlatformEnrichmentTokensSinceAsync(IndiaDayStart(), ct);
        if (ceiling is { } c0 && spentBefore >= c0)
        {
            LogCeiling(logger, spentBefore, c0);
            return new(0, 0, true);
        }

        var jobs = await db.Jobs.AsNoTracking().Where(j => missing.Contains(j.Id))
            .Select(j => new { j.Id, j.Title, j.CompanyName, j.LocationText, j.WorkMode, j.EmploymentType, j.SourceMeta, j.DescriptionText })
            .ToDictionaryAsync(j => j.Id, ct);

        long spent = 0;
        int created = 0, failed = 0, ceilingHit = 0;
        AiTaskFailedException? transient = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(missing.Where(jobs.ContainsKey),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.Parallelism), CancellationToken = stop.Token },
                async (id, token) =>
                {
                    if (ceiling is { } c && spentBefore + Interlocked.Read(ref spent) >= c)
                    {
                        Interlocked.Exchange(ref ceilingHit, 1);
                        return;
                    }
                    var j = jobs[id];
                    var countries = CoverageReport.Countries(j.SourceMeta);
                    var hint = EligibilityRules.Classify(countries, j.LocationText, j.Title, j.DescriptionText);
                    var input = JobEnrichmentTask.BuildInput(
                        new EnrichmentJobInput(j.Title, j.CompanyName, j.LocationText, j.WorkMode, j.EmploymentType, countries, j.DescriptionText),
                        hint);

                    await using var scope = scopes.CreateAsyncScope();
                    var runner = scope.ServiceProvider.GetRequiredService<IAiTaskRunner>();
                    AiTaskResult<EnrichmentOutput> result;
                    try
                    {
                        result = await runner.RunAsync(JobEnrichmentTask.Definition, input, userId: null, token);
                    }
                    catch (AiTaskFailedException ex) when (!ex.Transient)
                    {
                        Interlocked.Increment(ref failed);
                        LogFailed(logger, id, ex.Code);
                        return;
                    }
                    catch (AiTaskFailedException ex)
                    {
                        Interlocked.CompareExchange(ref transient, ex, null);
                        await stop.CancelAsync();
                        return;
                    }
                    Interlocked.Add(ref spent, result.CountedTokens);

                    var output = result.Value;
                    var evidence = JobEnrichmentTask.QuotedEvidence(output.EligibilityEvidence,
                        $"{j.Title}\n{j.LocationText}\n{j.DescriptionText}");
                    var resolved = EligibilityResolver.Resolve(hint, output.Eligibility, evidence);
                    var data = new EnrichmentData(JobEnrichmentTask.SchemaVersion, resolved.Source, output.Eligibility,
                        output with { Eligibility = resolved.Eligibility, EligibilityEvidence = resolved.Evidence });
                    var inserted = await InsertAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), id, data,
                        result.Model, result.PromptVersion, CancellationToken.None);
                    if (inserted) Interlocked.Increment(ref created);
                });
        }
        catch (OperationCanceledException) when (transient is not null && !ct.IsCancellationRequested)
        {
            // stopped because the provider is unavailable; reported below
        }

        if (transient is not null)
        {
            LogInterrupted(logger, created);
            throw transient;
        }
        if (ceilingHit == 1) LogCeiling(logger, spentBefore + spent, ceiling!.Value);
        LogEnriched(logger, created, failed, missing.Count);
        return new(created, failed, ceilingHit == 1);
    }

    /// <summary>Stores one enrichment; a row another run stored first wins. A job deleted meanwhile is skipped.</summary>
    private static async Task<bool> InsertAsync(AppDbContext db, Guid jobId, EnrichmentData data, string model, string promptVersion,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data, Json);
        try
        {
            return await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO job_enrichment (job_id, data, eligibility, model, prompt_version, schema_version)
                VALUES ({jobId}, {json}::jsonb, {data.Enrichment.Eligibility}, {model}, {promptVersion}, {data.SchemaVersion})
                ON CONFLICT (job_id) DO NOTHING
                """, ct) == 1;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return false;
        }
    }

    /// <summary>Start of today in Asia/Kolkata, as UTC (the ceiling's day, DatabaseDesign §7).</summary>
    private DateTimeOffset IndiaDayStart()
    {
        var india = TimeZones.RequireIana(TimeZones.India);
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), india).Date;
        return new DateTimeOffset(local, india.GetUtcOffset(local)).ToUniversalTime();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrichment: {Created} created, {Failed} failed, of {Missing} missing")]
    private static partial void LogEnriched(ILogger logger, int created, int failed, int missing);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrichment of job {JobId} failed with {ErrorCode}")]
    private static partial void LogFailed(ILogger logger, Guid jobId, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Platform enrichment ceiling reached ({Spent} of {Ceiling} tokens today); un-enriched jobs are ranked by rules")]
    private static partial void LogCeiling(ILogger logger, long spent, long ceiling);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrichment interrupted: AI provider unavailable after {Created} created")]
    private static partial void LogInterrupted(ILogger logger, int created);
}
