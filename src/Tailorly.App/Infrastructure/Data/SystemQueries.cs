using Microsoft.EntityFrameworkCore;

namespace Tailorly.App.Infrastructure.Data;

/// <summary>
/// The ONLY place allowed to call <c>IgnoreQueryFilters()</c> or to run raw SQL across users (DatabaseDesign §9). Each
/// method says which system process uses it and why it must see all users. Keep this list short and review every
/// addition.
///
/// Allowed callers: digest dispatcher (M3), purge job, stuck-work sweep, budget service, admin queries (M2 status and
/// companies pages; more in M6).
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

    /// <summary>Admin companies page: how many users watch each board (counts only, never who).</summary>
    public async Task<Dictionary<Guid, int>> WatcherCountsAsync(IReadOnlyCollection<Guid> linkIds, CancellationToken ct) =>
        await db.UserWatchedCompanies.IgnoreQueryFilters().AsNoTracking()
            .Where(w => linkIds.Contains(w.CompanyAtsLinkId))
            .GroupBy(w => w.CompanyAtsLinkId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    /// <summary>Admin status page: async resources still queued/running after the cutoff (what the sweep will fail).</summary>
    public Task<int> StuckResourceCountAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        db.Resumes.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(r => (r.ParseStatus == WorkStatus.Queued || r.ParseStatus == WorkStatus.Running) && r.CreatedAt < cutoff, ct);

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
