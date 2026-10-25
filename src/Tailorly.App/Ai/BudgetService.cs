using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Budgets;
using Tailorly.Core.Time;

namespace Tailorly.App.Ai;

public sealed class BudgetOptions
{
    public long DiscoveryTokens { get; set; } = 600_000;
    public long DocumentsTokens { get; set; } = 1_400_000;

    public long LimitFor(string bucket) => bucket switch
    {
        BudgetBuckets.Discovery => DiscoveryTokens,
        BudgetBuckets.Documents => DocumentsTokens,
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, "Not a per-user bucket."),
    };
}

/// <summary>
/// Monthly per-user token budgets (SDD §9.2): check remaining before a call, then atomically add the actual usage.
/// Counter rows are created lazily per period (<c>YYYY-MM</c> in Asia/Kolkata) with the configured limit, so a new
/// month needs no reset job. No reservations: a few concurrent calls may overshoot by at most their <c>max_tokens</c>.
/// Takes explicit user ids (the budget service is on the approved list in <see cref="SystemQueries"/>).
/// </summary>
public sealed class BudgetService(AppDbContext db, SystemQueries system, IOptions<BudgetOptions> options, TimeProvider clock)
{
    private static readonly TimeZoneInfo India = TimeZones.RequireIana(TimeZones.India);

    public string CurrentPeriod => PeriodKey.For(clock.GetUtcNow(), India);
    public DateOnly ResetsOn => PeriodKey.ResetsOn(clock.GetUtcNow(), India);

    /// <summary>Both per-user buckets for the current period (limits from config when no row exists yet).</summary>
    public async Task<IReadOnlyList<BudgetSnapshot>> GetAllAsync(Guid userId, CancellationToken ct)
    {
        var rows = (await system.BudgetCountersAsync(userId, CurrentPeriod, ct)).ToDictionary(c => c.Bucket);
        return BudgetBuckets.PerUser
            .Select(b => rows.TryGetValue(b, out var r)
                ? new BudgetSnapshot(b, r.LimitTokens, r.UsedTokens)
                : new BudgetSnapshot(b, options.Value.LimitFor(b), 0))
            .ToList();
    }

    public async Task<BudgetSnapshot> GetAsync(Guid userId, string bucket, CancellationToken ct) =>
        (await GetAllAsync(userId, ct)).Single(s => s.Bucket == bucket);

    /// <exception cref="BudgetExceededException">Nothing remains in the bucket this period.</exception>
    public async Task EnsureRemainingAsync(Guid userId, string bucket, CancellationToken ct)
    {
        if (!BudgetBuckets.IsPerUser(bucket)) return;
        var snapshot = await GetAsync(userId, bucket, ct);
        if (snapshot.IsExhausted) throw new BudgetExceededException(bucket, ResetsOn);
    }

    /// <summary>Adds actual usage atomically, creating the period row first if needed.</summary>
    public async Task AddAsync(Guid userId, string bucket, long countedTokens, CancellationToken ct)
    {
        if (!BudgetBuckets.IsPerUser(bucket) || countedTokens <= 0) return;
        var period = CurrentPeriod;
        var limit = options.Value.LimitFor(bucket);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_budget_counters (user_id, period_key, bucket, limit_tokens, used_tokens)
            VALUES ({userId}, {period}, {bucket}, {limit}, {countedTokens})
            ON CONFLICT (user_id, period_key, bucket)
            DO UPDATE SET used_tokens = user_budget_counters.used_tokens + EXCLUDED.used_tokens
            """, ct);
    }
}
