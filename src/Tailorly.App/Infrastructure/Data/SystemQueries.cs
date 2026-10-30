using Microsoft.EntityFrameworkCore;

namespace Tailorly.App.Infrastructure.Data;

/// <summary>
/// The ONLY place allowed to call <c>IgnoreQueryFilters()</c> or to run raw SQL across users (DatabaseDesign §9). Each
/// method says which system process uses it and why it must see all users. Keep this list short and review every
/// addition.
///
/// Allowed callers: digest dispatcher, purge job, stuck-work sweep, budget service, enrichment ceiling, admin queries (M2
/// status and companies pages; more in M6).
/// </summary>
public sealed class SystemQueries(AppDbContext db)
{
    /// <summary>Budget service: a user's counters for one period, by explicit user id (also used outside a request).</summary>
    public Task<List<UserBudgetCounter>> BudgetCountersAsync(Guid userId, string periodKey, CancellationToken ct) =>
        db.UserBudgetCounters.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.UserId == userId && c.PeriodKey == periodKey)
            .ToListAsync(ct);

    /// <summary>
    /// Budget service: atomically adds actual usage to a user's counter by explicit user id (the AI runner charges the user
    /// a job acts for), creating the period row first if needed.
    /// </summary>
    public Task<int> AddBudgetUsageAsync(Guid userId, string periodKey, string bucket, long limitTokens, long countedTokens,
        CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_budget_counters (user_id, period_key, bucket, limit_tokens, used_tokens)
            VALUES ({userId}, {periodKey}, {bucket}, {limitTokens}, {countedTokens})
            ON CONFLICT (user_id, period_key, bucket)
            DO UPDATE SET used_tokens = user_budget_counters.used_tokens + EXCLUDED.used_tokens
            """, ct);

    /// <summary>
    /// Stuck-work sweep (every 15 min, DatabaseDesign §7): resources still queued/running after the cutoff are marked
    /// failed with <c>TIMEOUT</c> so the user can retry. Returns the number of rows changed.
    /// </summary>
    public Task<int> FailStuckResumesAsync(DateTimeOffset cutoff, string timeoutCode, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE resumes SET parse_status = 'failed', error_code = {timeoutCode}
            WHERE parse_status IN ('queued','running') AND created_at < {cutoff}
            """, ct);

    /// <summary>
    /// Digest dispatcher (every 5 min, DatabaseDesign §7): users due for their scheduled digest at <paramref name="now"/>
    /// (active, verified, digest enabled, an active ready résumé) and the local date it belongs to. The time-zone maths is
    /// done in PostgreSQL. A window that crosses midnight (schedule 22:00 + 4 h) is handled by also testing yesterday's
    /// local date: the digest belongs to the day its schedule time fell on. Users whose time zone PostgreSQL does not know
    /// are skipped instead of failing the whole query.
    /// </summary>
    public async Task<List<(Guid UserId, DateOnly LocalDate)>> DueScheduledDigestsAsync(DateTimeOffset now, int windowHours,
        CancellationToken ct)
    {
        var due = new List<(Guid, DateOnly)>();
        await using var cmd = await CommandAsync("""
            WITH tz AS MATERIALIZED (
                SELECT p.user_id, p.time_zone
                FROM user_profiles p
                WHERE p.account_status = 'active'
                  AND p.time_zone IN (SELECT name FROM pg_timezone_names)
            ), cand AS (
                SELECT tz.user_id, pr.schedule_local_time, (@now::timestamptz AT TIME ZONE tz.time_zone) AS local_now
                FROM tz
                JOIN user_preferences pr ON pr.user_id = tz.user_id AND pr.schedule_enabled
                JOIN asp_net_users u     ON u.id = tz.user_id AND u.email_confirmed
                WHERE EXISTS (SELECT 1 FROM resumes r WHERE r.user_id = tz.user_id AND r.is_active AND r.parse_status = 'ready')
            )
            SELECT c.user_id, d.local_date
            FROM cand c
            CROSS JOIN LATERAL (VALUES (c.local_now::date), (c.local_now::date - 1)) AS d(local_date)
            WHERE c.local_now >= d.local_date + c.schedule_local_time
              AND c.local_now <  d.local_date + c.schedule_local_time + make_interval(hours => @window)
              AND NOT EXISTS (SELECT 1 FROM digests g
                              WHERE g.user_id = c.user_id AND g.kind = 'scheduled' AND g.local_date = d.local_date)
            """, ct, ("now", now), ("window", windowHours));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            due.Add((reader.GetGuid(0), DateOnly.FromDateTime(reader.GetDateTime(1))));
        return due;
    }

    /// <summary>
    /// Digest dispatcher: claim = insert (SDD §11.1). Only the caller whose insert returns a row may enqueue the run, so
    /// two ticks or two instances create exactly one scheduled digest per user and local date.
    /// </summary>
    public async Task<bool> ClaimScheduledDigestAsync(Guid digestId, Guid userId, DateOnly localDate, CancellationToken ct) =>
        (await db.Database.SqlQuery<Guid>($"""
            INSERT INTO digests (id, user_id, kind, local_date, status)
            VALUES ({digestId}, {userId}, 'scheduled', {localDate}, 'queued')
            ON CONFLICT (user_id, local_date) WHERE kind = 'scheduled' DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(ct)).Count == 1;

    /// <summary>Enrichment: platform tokens spent on <c>job_enrichment</c> since a time (the daily ceiling, DatabaseDesign §7).</summary>
    public Task<long> PlatformEnrichmentTokensSinceAsync(DateTimeOffset since, CancellationToken ct) =>
        db.AiUsage.AsNoTracking()
            .Where(u => u.UserId == null && u.Task == "job_enrichment" && u.CreatedAt >= since)
            .SumAsync(u => (long)u.CountedTokens, ct);

    /// <summary>
    /// Stuck-work sweep: digest runs still queued/running after the cutoff (measured from the current attempt's start)
    /// become <c>failed</c>/<c>TIMEOUT</c>.
    /// </summary>
    public Task<int> FailStuckDigestsAsync(DateTimeOffset cutoff, string timeoutCode, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE digests SET status = 'failed', error_code = {timeoutCode}, finished_at = now()
            WHERE status IN ('queued','running') AND coalesce(started_at, created_at) < {cutoff}
            """, ct);

    /// <summary>Admin status page: digest runs since a time (counts only), and ready scheduled digests still unsent.</summary>
    public async Task<(int Ready, int Failed, int Running, int Degraded, int Unsent, DateTimeOffset? LastFinished)> DigestRunStatsAsync(
        DateTimeOffset since, DateTimeOffset unsentBefore, CancellationToken ct)
    {
        var rows = db.Digests.IgnoreQueryFilters().AsNoTracking().Where(d => d.CreatedAt >= since);
        var byStatus = await rows.GroupBy(d => d.Status).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var degraded = await rows.CountAsync(d => d.Degraded, ct);
        var unsent = await db.Digests.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(d => d.Kind == DigestKinds.Scheduled && d.Status == WorkStatus.Ready && d.EmailedAt == null
                             && d.FinishedAt < unsentBefore
                             && db.DigestItems.IgnoreQueryFilters().Any(i => i.DigestId == d.Id), ct);
        var last = await db.Digests.IgnoreQueryFilters().AsNoTracking().MaxAsync(d => d.FinishedAt, ct);
        return (byStatus.GetValueOrDefault(WorkStatus.Ready), byStatus.GetValueOrDefault(WorkStatus.Failed),
            byStatus.GetValueOrDefault(WorkStatus.Queued) + byStatus.GetValueOrDefault(WorkStatus.Running), degraded, unsent, last);
    }

    /// <summary>A parameterised command on the context's connection (for multi-column reads without a model type).</summary>
    private async Task<System.Data.Common.DbCommand> CommandAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.Add(new Npgsql.NpgsqlParameter(name, value));
        return cmd;
    }

    /// <summary>Admin companies page: how many users watch each board (counts only, never who).</summary>
    public async Task<Dictionary<Guid, int>> WatcherCountsAsync(IReadOnlyCollection<Guid> linkIds, CancellationToken ct) =>
        await db.UserWatchedCompanies.IgnoreQueryFilters().AsNoTracking()
            .Where(w => linkIds.Contains(w.CompanyAtsLinkId))
            .GroupBy(w => w.CompanyAtsLinkId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    /// <summary>Admin status page: async resources still queued/running after the cutoff (what the sweep will fail).</summary>
    public async Task<int> StuckResourceCountAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        await db.Resumes.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(r => (r.ParseStatus == WorkStatus.Queued || r.ParseStatus == WorkStatus.Running) && r.CreatedAt < cutoff, ct)
        + await db.Digests.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(d => (d.Status == WorkStatus.Queued || d.Status == WorkStatus.Running) && (d.StartedAt ?? d.CreatedAt) < cutoff, ct);

    /// <summary>Purge job (hourly): accounts confirmed for deletion that still exist.</summary>
    public Task<List<Guid>> UsersPendingDeletionAsync(CancellationToken ct) =>
        db.UserProfiles.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.AccountStatus == AccountStatus.PendingDeletion)
            .Select(p => p.UserId)
            .ToListAsync(ct);

    /// <summary>Purge job: account status of one user (the job runs outside any request).</summary>
    public Task<string?> AccountStatusAsync(Guid userId, CancellationToken ct) =>
        db.UserProfiles.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => p.AccountStatus)
            .FirstOrDefaultAsync(ct);

    /// <summary>Purge job: every stored file of a user (DatabaseDesign §8 step 1). Tailored documents join in M4.</summary>
    public Task<List<string>> StorageKeysOfUserAsync(Guid userId, CancellationToken ct) =>
        db.Resumes.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.UserId == userId)
            .Select(r => r.StorageKey)
            .ToListAsync(ct);

    /// <summary>
    /// Purge job: deletes the Identity user; <c>ON DELETE CASCADE</c> removes every user-owned row and
    /// <c>ai_usage.user_id</c> / <c>audit_log.actor_user_id</c> become NULL (DatabaseDesign §8 step 3).
    /// </summary>
    public Task<int> DeleteUserCascadeAsync(Guid userId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM asp_net_users WHERE id = {userId}", ct);
}
