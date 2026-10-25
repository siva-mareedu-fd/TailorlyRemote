using Microsoft.EntityFrameworkCore;

namespace Tailorly.App.Infrastructure.Data;

/// <summary>
/// The ONLY place allowed to call <c>IgnoreQueryFilters()</c> or to run raw SQL across users (DatabaseDesign §9). Each
/// method says which system process uses it and why it must see all users. Keep this list short and review every
/// addition.
///
/// Allowed callers: digest dispatcher (M3), purge job, stuck-work sweep, budget service, admin queries (M6).
/// </summary>
public sealed class SystemQueries(AppDbContext db)
{
    internal AppDbContext Db => db;

    /// <summary>Budget service: a user's counters for one period, by explicit user id (also used outside a request).</summary>
    public Task<List<UserBudgetCounter>> BudgetCountersAsync(Guid userId, string periodKey, CancellationToken ct) =>
        db.UserBudgetCounters.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.UserId == userId && c.PeriodKey == periodKey)
            .ToListAsync(ct);

    /// <summary>
    /// Stuck-work sweep (every 15 min, DatabaseDesign §7): resources still queued/running after the cutoff are marked
    /// failed with <c>TIMEOUT</c> so the user can retry. Returns the number of rows changed.
    /// </summary>
    public Task<int> FailStuckResumesAsync(DateTimeOffset cutoff, string timeoutCode, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE resumes SET parse_status = 'failed', error_code = {timeoutCode}
            WHERE parse_status IN ('queued','running') AND created_at < {cutoff}
            """, ct);

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
