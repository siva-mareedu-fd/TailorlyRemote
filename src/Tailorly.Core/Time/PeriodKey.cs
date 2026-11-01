using System.Globalization;

namespace Tailorly.Core.Time;

/// <summary>Budget period key <c>YYYY-MM</c> in Asia/Kolkata (PRD §6.3; no destructive reset job).</summary>
public static class PeriodKey
{
    public static string For(DateTimeOffset utcNow, TimeZoneInfo india)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, india);
        return local.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }

    /// <summary>Start of the local day that contains <paramref name="utcNow"/>, as UTC (the enrichment ceiling's day).</summary>
    public static DateTimeOffset DayStartUtc(DateTimeOffset utcNow, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, zone).Date;
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>First day of the next period, as a local date (shown to the user as "resets on").</summary>
    public static DateOnly ResetsOn(DateTimeOffset utcNow, TimeZoneInfo india)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, india);
        return new DateOnly(local.Year, local.Month, 1).AddMonths(1);
    }
}
