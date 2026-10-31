namespace Tailorly.App.Infrastructure.Logging;

/// <summary>
/// Property names that must never reach the logs (SDD §14.3): <c>resume*</c>, <c>description*</c>,
/// <c>content*</c>, <c>email</c>, <c>prompt</c>, <c>response</c>. Matching is case-insensitive.
/// Names ending in <c>email</c> (e.g. <c>NotificationEmail</c>) are blocked too, stricter than the doc requires, and so
/// are <c>QueryString</c> and names ending in <c>token</c>: the one-click unsubscribe link carries its signed token in the
/// query string (ApiDesign §4.10), which ASP.NET's own request events would otherwise log. File names are blocked as well
/// (names ending in <c>filename</c>, and ASP.NET's <c>FileDownloadName</c>): a résumé's file name usually carries the
/// person's name, and ASP.NET logs it at Debug for every download.
/// </summary>
public static class PiiFields
{
    private static readonly string[] Prefixes = ["resume", "description", "content"];
    private static readonly string[] Suffixes = ["email", "token", "filename"];
    private static readonly string[] Exact = ["email", "prompt", "response", "querystring", "filedownloadname"];

    public static bool IsBlocked(string propertyName)
    {
        foreach (var p in Prefixes)
            if (propertyName.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var s in Suffixes)
            if (propertyName.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var e in Exact)
            if (propertyName.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
