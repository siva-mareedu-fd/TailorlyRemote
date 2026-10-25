using System.Text.RegularExpressions;

namespace Tailorly.Core.Jobs;

public static class AtsPlatforms
{
    public const string Greenhouse = "greenhouse";
    public const string Lever = "lever";
    public const string Ashby = "ashby";
    public static readonly string[] All = [Greenhouse, Lever, Ashby];
}

/// <summary>A board (and optionally one job on it) found in a URL.</summary>
public sealed record AtsBoardRef(string Platform, string BoardToken, string? JobId);

/// <summary>
/// Parses employer ATS URLs (PRD §7.3 step 1, ApiDesign §4.5) without fetching them. Only the exact allowlisted hosts are
/// accepted, over http(s) on the default port, with no user info; the token must match <c>^[a-z0-9_-]{2,60}$</c>. The
/// URL itself is never requested: callers place the token into an adapter's fixed API URL template (SDD §14.2, SSRF).
/// </summary>
public static partial class AtsUrl
{
    private static readonly Dictionary<string, string> Hosts = new(StringComparer.Ordinal)
    {
        ["boards.greenhouse.io"] = AtsPlatforms.Greenhouse,
        ["job-boards.greenhouse.io"] = AtsPlatforms.Greenhouse,
        ["jobs.lever.co"] = AtsPlatforms.Lever,
        ["jobs.ashbyhq.com"] = AtsPlatforms.Ashby,
    };

    /// <summary>True for a token that may go into a fixed URL template.</summary>
    public static bool IsValidToken(string? token) => token is not null && Token().IsMatch(token);

    /// <summary>The token if valid, else an exception: the last line of defence before a URL is built.</summary>
    public static string RequireToken(string? token) =>
        IsValidToken(token) ? token! : throw new ArgumentException("Board token must match ^[a-z0-9_-]{2,60}$.", nameof(token));

    public static bool IsPlatform(string? platform) => platform is AtsPlatforms.Greenhouse or AtsPlatforms.Lever or AtsPlatforms.Ashby;

    public static bool TryParse(string? url, out AtsBoardRef board)
    {
        board = null!;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("https" or "http") || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return false;
        if (!Hosts.TryGetValue(uri.IdnHost.ToLowerInvariant(), out var platform)) return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        string? token;
        string? jobId = null;

        if (platform == AtsPlatforms.Greenhouse && segments is ["embed", ..])
        {
            // boards.greenhouse.io/embed/job_board?for={token}  or  /embed/job_app?for={token}&token={jobId}
            var query = ParseQuery(uri.Query);
            token = query.GetValueOrDefault("for");
            if (segments is [_, "job_app"]) jobId = query.GetValueOrDefault("token");
        }
        else
        {
            if (segments.Length == 0) return false;
            token = segments[0];
            jobId = platform switch
            {
                AtsPlatforms.Greenhouse when segments is [_, "jobs", var id, ..] => id,
                AtsPlatforms.Lever or AtsPlatforms.Ashby when segments.Length >= 2 => segments[1],
                _ => null,
            };
        }

        token = token?.ToLowerInvariant();
        if (!IsValidToken(token)) return false;
        if (jobId is not null && !JobId().IsMatch(jobId)) jobId = null;
        board = new AtsBoardRef(platform, token!, jobId);
        return true;
    }

    /// <summary>Every ATS link in a block of HTML or text (hrefs and bare URLs), de-duplicated by board.</summary>
    public static IReadOnlyList<AtsBoardRef> FindAll(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var found = new Dictionary<(string, string), AtsBoardRef>();
        foreach (Match m in Url().Matches(text.Length > 200_000 ? text[..200_000] : text))
            if (TryParse(System.Net.WebUtility.HtmlDecode(m.Value).TrimEnd('.', ',', ';', ':', '!', '?'), out var b))
                found.TryAdd((b.Platform, b.BoardToken), b);
        return found.Values.ToList();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2) d.TryAdd(Uri.UnescapeDataString(kv[0]), Uri.UnescapeDataString(kv[1]));
        }
        return d;
    }

    [GeneratedRegex("^[a-z0-9_-]{2,60}$", RegexOptions.None, 1000)]
    private static partial Regex Token();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,80}$", RegexOptions.None, 1000)]
    private static partial Regex JobId();

    [GeneratedRegex(@"https?://(?:boards|job-boards)\.greenhouse\.io/[^\s""'<>)]+|https?://jobs\.(?:lever\.co|ashbyhq\.com)/[^\s""'<>)]+",
        RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Url();
}
