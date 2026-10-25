using System.Globalization;

namespace Tailorly.Core.Budgets;

/// <summary>Budget buckets (PRD §6.3). Only discovery and documents are per user; platform is a shared cost.</summary>
public static class BudgetBuckets
{
    public const string Discovery = "discovery";
    public const string Documents = "documents";
    public const string Platform = "platform";

    public static readonly IReadOnlyList<string> PerUser = [Discovery, Documents];

    public static bool IsPerUser(string bucket) => bucket is Discovery or Documents;
}

/// <summary>One user's bucket for one period.</summary>
public sealed record BudgetSnapshot(string Bucket, long Limit, long Used)
{
    public long Remaining => Math.Max(0, Limit - Used);

    /// <summary>Whole percent used, capped at 100. A zero limit counts as fully used.</summary>
    public int PercentUsed => Limit <= 0 ? 100 : (int)Math.Min(100, Used * 100 / Limit);

    /// <summary>Refuse new calls once nothing remains (check-then-increment, SDD §9.2).</summary>
    public bool IsExhausted => Remaining <= 0;
}

/// <summary>Pure budget rules: warnings and limits. Enforcement (atomic counters) lives in the App's BudgetService.</summary>
public static class BudgetPolicy
{
    public const int WarningPercent = 80;

    /// <summary>
    /// Warning keys for the SPA banner: <c>{bucket}_80_percent</c> at ≥ 80 % and <c>{bucket}_100_percent</c> when
    /// exhausted (only the stronger one per bucket). Ordered as the snapshots are.
    /// </summary>
    public static IReadOnlyList<string> Warnings(IEnumerable<BudgetSnapshot> buckets)
    {
        var list = new List<string>();
        foreach (var b in buckets)
        {
            if (b.IsExhausted) list.Add(b.Bucket + "_100_percent");
            else if (b.PercentUsed >= WarningPercent) list.Add(b.Bucket + "_80_percent");
        }
        return list;
    }

    /// <summary>ISO date (<c>YYYY-MM-DD</c>) of the next reset, for <c>extra.resetsOn</c>.</summary>
    public static string FormatResetsOn(DateOnly resetsOn) => resetsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
