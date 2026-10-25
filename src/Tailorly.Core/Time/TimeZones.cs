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

    /// <summary>True if <paramref name="ianaId"/> (or the canonical name of a legacy alias) names a resolvable time zone.</summary>
    public static bool IsValid(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId)) return false;
        return TimeZoneInfo.TryFindSystemTimeZoneById(Canonicalize(ianaId), out _);
    }

    /// <summary>
    /// Maps legacy IANA aliases to their canonical names. Browsers still report some aliases (Chromium says
    /// <c>Asia/Calcutta</c> for India), while slim tzdata packages ship only canonical names.
    /// </summary>
    public static string Canonicalize(string ianaId)
    {
        var id = ianaId.Trim();
        return LegacyAliases.TryGetValue(id, out var canonical) ? canonical : id;
    }

    private static readonly Dictionary<string, string> LegacyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Asia/Calcutta"] = "Asia/Kolkata",
        ["Asia/Katmandu"] = "Asia/Kathmandu",
        ["Asia/Saigon"] = "Asia/Ho_Chi_Minh",
        ["Asia/Rangoon"] = "Asia/Yangon",
        ["Asia/Dacca"] = "Asia/Dhaka",
        ["Asia/Thimbu"] = "Asia/Thimphu",
        ["Asia/Ulan_Bator"] = "Asia/Ulaanbaatar",
        ["Europe/Kiev"] = "Europe/Kyiv",
        ["America/Buenos_Aires"] = "America/Argentina/Buenos_Aires",
        ["America/Indianapolis"] = "America/Indiana/Indianapolis",
        ["Pacific/Truk"] = "Pacific/Chuuk",
        ["Atlantic/Faeroe"] = "Atlantic/Faroe",
    };
}
