namespace Tailorly.App.Infrastructure.Logging;

/// <summary>
/// Property names that must never reach the logs (SDD §14.3): <c>resume*</c>, <c>description*</c>,
/// <c>content*</c>, <c>email</c>, <c>prompt</c>, <c>response</c>. Matching is case-insensitive.
/// Names ending in <c>email</c> (e.g. <c>NotificationEmail</c>) are blocked too, stricter than the doc requires.
/// </summary>
public static class PiiFields
{
    private static readonly string[] Prefixes = ["resume", "description", "content"];
    private static readonly string[] Exact = ["email", "prompt", "response"];

    public static bool IsBlocked(string propertyName)
    {
        foreach (var p in Prefixes)
            if (propertyName.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        if (propertyName.EndsWith("email", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var e in Exact)
            if (propertyName.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
