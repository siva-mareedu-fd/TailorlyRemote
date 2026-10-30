using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Ports;
using Tailorly.Core.Time;

namespace Tailorly.App.Features.Sources;

/// <summary>The adapters registered in DI, by source key.</summary>
public sealed class SourceRegistry(IEnumerable<IJobSource> sources)
{
    private readonly Dictionary<string, IJobSource> _byKey = sources.ToDictionary(s => s.SourceKey, StringComparer.Ordinal);

    public IJobSource? Get(string key) => _byKey.GetValueOrDefault(key);
    public IBoardSource? Board(string platform) => Get(platform) as IBoardSource;
    public IReadOnlyCollection<string> Keys => _byKey.Keys;
}

/// <summary>
/// <c>FetchSources</c> (PRD §9.4): every 10 minutes, enqueues one isolated <see cref="FetchSourceJob"/> per enabled source
/// that is due (<c>poll_interval_minutes</c> after its last run; 60 minutes after a failure) and whose circuit is closed.
/// The interval lives in <c>job_sources</c>, so an admin change applies on the next tick without re-registering jobs.
/// </summary>
public sealed class FetchSourcesJob(AppDbContext db, SourceRegistry registry, IBackgroundJobClient jobs, TimeProvider clock)
{
    public const string RecurringId = "fetch-sources";
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(60);

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rows = await (from s in db.JobSources.AsNoTracking()
                          join h in db.SourceHealth.AsNoTracking() on s.Key equals h.SourceKey
                          where s.Enabled && (s.Kind == SourceKinds.Feed || s.Kind == SourceKinds.Board)
                          select new { s.Key, s.PollIntervalMinutes, Health = h }).ToListAsync(ct);
        var due = new List<string>();
        foreach (var r in rows.Where(r => registry.Get(r.Key) is not null))
        {
            var h = r.Health;
            if (h.CircuitOpenUntil > now) continue;
            var failedLast = h.LastFailureAt is { } f && (h.LastSuccessAt is null || f > h.LastSuccessAt);
            var last = failedLast ? h.LastFailureAt : h.LastSuccessAt;
            var wait = failedLast ? TimeSpan.FromMinutes(Math.Min(r.PollIntervalMinutes, RetryAfterFailure.TotalMinutes))
                                  : TimeSpan.FromMinutes(r.PollIntervalMinutes);
            if (last is null || last + wait <= now) due.Add(r.Key);
        }
        foreach (var key in due)
            jobs.Enqueue<FetchSourceJob>(j => j.RunAsync(key, false, CancellationToken.None));
        return due;
    }
}

/// <summary>
/// Fetches one source, isolated (SDD §8.1, PRD §7.2): the run is wrapped in try/catch and always writes
/// <c>source_health</c>; a failure never throws to Hangfire, so it never fails the cycle or another source. A 429/403 opens
/// the circuit until <c>Retry-After</c> or <c>Sources:CircuitOpenMinutes</c>. A PostgreSQL session advisory lock keeps
/// a source from running twice at once.
/// </summary>
public sealed partial class FetchSourceJob(AppDbContext db, SourceRegistry registry, FeedRunner feeds, BoardPoller boards,
    SourceCallCounter calls, IOptions<SourcesOptions> options, IConfiguration config, TimeProvider clock, ILogger<FetchSourceJob> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [AutomaticRetry(Attempts = 0)]
    public async Task<RunStats?> RunAsync(string sourceKey, bool manual, CancellationToken ct)
    {
        await using var lockConn = new NpgsqlConnection(config.GetConnectionString("Default"));
        await lockConn.OpenAsync(ct);
        if (!await LockAsync(lockConn, "pg_try_advisory_lock", sourceKey, ct))
        {
            LogAlreadyRunning(logger, sourceKey);
            return null;
        }
        try
        {
            return await RunLockedAsync(sourceKey, manual, ct);
        }
        finally
        {
            // Pooled connections keep session locks until they are reset, so release explicitly.
            await LockAsync(lockConn, "pg_advisory_unlock", sourceKey, CancellationToken.None);
        }
    }

    private async Task<RunStats?> RunLockedAsync(string sourceKey, bool manual, CancellationToken ct)
    {
        var source = await db.JobSources.AsNoTracking().FirstOrDefaultAsync(s => s.Key == sourceKey, ct);
        var health = await db.SourceHealth.FirstOrDefaultAsync(h => h.SourceKey == sourceKey, ct);
        if (source is null || health is null || !source.Enabled) return null;
        var start = clock.GetUtcNow();
        if (!manual && health.CircuitOpenUntil > start) return null;
        calls.Take(sourceKey); // count only this run's calls

        RunStats? stats = null;
        string? error = null;
        TimeSpan? openFor = null;
        try
        {
            stats = registry.Get(sourceKey) switch
            {
                IFeedSource feed => await feeds.RunAsync(source, health, feed, ct),
                IBoardSource board => await boards.RunAsync(source, board, ct),
                _ => throw new SourceException(SourceFailure.Unavailable, "No adapter for this source"),
            };
        }
        catch (SourceException ex)
        {
            error = $"{ex.Failure}: {ex.Message}";
            if (ex.Failure == SourceFailure.Throttled)
            {
                var floor = TimeSpan.FromMinutes(options.Value.CircuitOpenMinutes);
                openFor = ex.RetryAfter is { } ra && ra > floor ? ra : floor;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error = "Unexpected " + ex.GetType().Name;
            LogCrashed(logger, ex, sourceKey);
        }

        // The feed runner leaves the new cursors on the instance it was given; keep them only on success. Reload the row:
        // runners may have cleared the change tracker after a failed board.
        var cursor = health.LastCursor;
        db.ChangeTracker.Clear();
        health = await db.SourceHealth.FirstAsync(h => h.SourceKey == sourceKey, CancellationToken.None);
        var now = clock.GetUtcNow();
        if (error is null)
        {
            health.LastSuccessAt = start;
            health.LastCursor = cursor;
            health.ConsecutiveFailures = 0;
            health.CircuitOpenUntil = null;
        }
        else
        {
            health.LastFailureAt = now;
            health.LastError = error.Length <= 300 ? error : error[..300];
            health.ConsecutiveFailures++;
            if (openFor is { } d) health.CircuitOpenUntil = now + d;
        }
        var period = PeriodKey.For(now, TimeZones.RequireIana(TimeZones.India));
        if (health.CallsPeriod != period)
        {
            health.CallsPeriod = period;
            health.CallsThisMonth = 0;
        }
        var made = calls.Take(sourceKey);
        health.CallsThisMonth += made;
        if (stats is not null) stats.Requests = made;
        health.LastRunStats = JsonSerializer.Serialize(new
        {
            startedAt = start, finishedAt = now, ok = error is null, manual, requests = made,
            stats?.Pages, stats?.Fetched, stats?.New, stats?.Updated, stats?.Unchanged, stats?.Skipped, stats?.Closed,
            stats?.Discovered, stats?.Boards, stats?.BoardsFailed, stats?.BoardsNotFound, stops = stats?.Stops,
        }, Json);
        await db.SaveChangesAsync(CancellationToken.None);

        if (error is null) LogFetched(logger, sourceKey, stats!.Fetched, stats.New, made);
        else LogFailed(logger, sourceKey, error, health.ConsecutiveFailures);
        return stats;
    }

    private static async Task<bool> LockAsync(NpgsqlConnection conn, string function, string key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {function}(hashtextextended(@k, 0))", conn);
        cmd.Parameters.AddWithValue("k", "fetch:" + key);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Source {SourceKey} already running; skipped")]
    private static partial void LogAlreadyRunning(ILogger logger, string sourceKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Source {SourceKey} fetched {Fetched} listings, {New} new, {Requests} requests")]
    private static partial void LogFetched(ILogger logger, string sourceKey, int fetched, int @new, int requests);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source {SourceKey} failed: {Error} ({Failures} in a row)")]
    private static partial void LogFailed(ILogger logger, string sourceKey, string error, int failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "Source {SourceKey} run crashed")]
    private static partial void LogCrashed(ILogger logger, Exception ex, string sourceKey);
}

/// <summary>
/// One board, now: a board just added to a watch list, by an admin, or discovered from a feed (eager resolution, PRD §7.3
/// step 5). Skipped while its platform source is disabled or its circuit is open; the regular cycle picks it up later.
/// </summary>
public sealed class PollBoardJob(AppDbContext db, SourceRegistry registry, BoardPoller poller, TimeProvider clock)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid linkId, CancellationToken ct)
    {
        var platform = await db.CompanyAtsLinks.AsNoTracking().Where(l => l.Id == linkId).Select(l => l.Platform).FirstOrDefaultAsync(ct);
        if (platform is null || registry.Board(platform) is not { } adapter) return;
        var source = await db.JobSources.AsNoTracking().FirstAsync(s => s.Key == platform, ct);
        var health = await db.SourceHealth.AsNoTracking().FirstAsync(h => h.SourceKey == platform, ct);
        if (!source.Enabled || health.CircuitOpenUntil > clock.GetUtcNow()) return;
        await poller.PollAsync(source, adapter, linkId, new RunStats(), ct);
    }
}

/// <summary>
/// Retention sweeps (DatabaseDesign §7, SDD §8.1 "Expiry"). Hourly: listings of <c>purge_on_expiry</c> sources (Himalayas)
/// are deleted once their <c>expires_at</c> passes. Daily: listings older than 60 days, listings closed for 7 days, and
/// digests and audit rows older than 12 months. Deleting a listing cascades to its enrichment and scores; tracker cards keep their
/// snapshot (M5). Survivors sharing a dedupe key get a fresh canonical pick.
/// </summary>
public sealed partial class RetentionJob(AppDbContext db, SystemQueries system, DedupeResolver dedupe, TimeProvider clock,
    ILogger<RetentionJob> logger)
{
    public const string ExpiryRecurringId = "purge-expired-jobs";
    public const string DailyRecurringId = "retention-sweep";

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var keys = await db.Database.SqlQuery<string>($"""
            DELETE FROM jobs j USING job_sources s
            WHERE s.key = j.source_key AND s.purge_on_expiry AND j.expires_at IS NOT NULL AND j.expires_at < {now}
            RETURNING j.dedupe_key AS "Value"
            """).ToListAsync(ct);
        await dedupe.ResolveAsync(keys.ToHashSet(StringComparer.Ordinal), ct);
        if (keys.Count > 0) LogPurged(logger, "expired", keys.Count);
        return keys.Count;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public async Task<int> RunDailyAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var total = await PurgeExpiredAsync(ct);
        var old = await db.Database.SqlQuery<string>($"""
            DELETE FROM jobs WHERE coalesce(posted_at, created_at) < {now.AddDays(-60)} RETURNING dedupe_key AS "Value"
            """).ToListAsync(ct);
        var closed = await db.Database.SqlQuery<string>($"""
            DELETE FROM jobs WHERE closed_at < {now.AddDays(-7)} RETURNING dedupe_key AS "Value"
            """).ToListAsync(ct);
        await dedupe.ResolveAsync(old.Concat(closed).ToHashSet(StringComparer.Ordinal), ct);
        var digests = await system.DeleteDigestsCreatedBeforeAsync(now.AddMonths(-12), ct);
        var audit = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM audit_log WHERE at < {now.AddMonths(-12)}", ct);
        LogPurged(logger, "older than 60 days", old.Count);
        LogPurged(logger, "closed over 7 days", closed.Count);
        LogPurged(logger, "digests over 12 months", digests);
        LogPurged(logger, "audit rows over 12 months", audit);
        return total + old.Count + closed.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention: deleted {Count} {What}")]
    private static partial void LogPurged(ILogger logger, string what, int count);
}
