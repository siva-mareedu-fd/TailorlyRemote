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

    /// <summary>First day of the next period, as a local date (shown to the user as "resets on").</summary>
    public static DateOnly ResetsOn(DateTimeOffset utcNow, TimeZoneInfo india)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, india);
        return new DateOnly(local.Year, local.Month, 1).AddMonths(1);
    }
}
