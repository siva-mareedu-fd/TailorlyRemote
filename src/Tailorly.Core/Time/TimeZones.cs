namespace Tailorly.Core.Time;

public static class TimeZones
{
    /// <summary>Default user time zone and the zone that defines budget periods (PRD §6.3).</summary>
    public const string India = "Asia/Kolkata";

    /// <summary>
    /// Resolves an IANA time zone or throws. Used by the startup self-test: the app must fail fast
    /// when the container lacks tzdata/ICU (SDD §13.2).
    /// </summary>
    public static TimeZoneInfo RequireIana(string ianaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ianaId);
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"Time zone '{ianaId}' could not be resolved. Install tzdata/ICU in the runtime image.", ex);
        }
    }

    /// <summary>True if <paramref name="ianaId"/> names a resolvable time zone.</summary>
    public static bool IsValid(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId)) return false;
        return TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out _);
    }
}
