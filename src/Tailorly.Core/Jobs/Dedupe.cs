using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Tailorly.Core.Jobs;

/// <summary>Name normalisation shared by dedupe (SDD §8.3) and company lookup (PRD §7.3 step 2).</summary>
public static partial class NameNormalizer
{
    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.Ordinal)
    {
        "inc", "incorporated", "llc", "llp", "lp", "ltd", "limited", "pvt", "private", "gmbh", "corp", "corporation",
        "co", "company", "plc", "bv", "ag", "sa", "sas", "srl", "spa", "pty", "oy", "ab", "as", "kk", "nv", "pte",
    };

    private static readonly Dictionary<string, string> TitleSynonyms = new(StringComparer.Ordinal)
    {
        ["sr"] = "senior", ["snr"] = "senior", ["jr"] = "junior", ["jnr"] = "junior",
        ["eng"] = "engineer", ["engr"] = "engineer", ["dev"] = "developer", ["mgr"] = "manager", ["swe"] = "software engineer",
        ["sde"] = "software engineer", ["ii"] = "2", ["iii"] = "3", ["iv"] = "4",
    };

    /// <summary>Words that say where or how, not what: dropped so "Backend Engineer (Remote)" matches "Backend Engineer".</summary>
    private static readonly HashSet<string> TitleNoise = new(StringComparer.Ordinal)
    {
        "remote", "hybrid", "wfh", "anywhere", "worldwide", "global", "fully", "100",
    };

    /// <summary>Lower-case, punctuation removed, legal suffixes (inc, llc, ltd, pvt, gmbh…) stripped from the end.</summary>
    public static string Company(string? name)
    {
        var words = Words(name?.Replace("&", " and ", StringComparison.Ordinal));
        if (words.Count > 1 && words[0] == "the") words.RemoveAt(0);
        while (words.Count > 1 && LegalSuffixes.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    /// <summary>Lower-case, punctuation removed, seniority/role synonyms collapsed ("Sr." → "senior"), location noise dropped.</summary>
    public static string Title(string? title)
    {
        var words = Words(title)
            .Select(w => TitleSynonyms.TryGetValue(w, out var s) ? s : w)
            .Where(w => !TitleNoise.Contains(w))
            .ToList();
        // "Back End" / "Front-End" → "backend" / "frontend"
        var joined = string.Join(' ', words)
            .Replace("back end", "backend", StringComparison.Ordinal)
            .Replace("front end", "frontend", StringComparison.Ordinal)
            .Replace("full stack", "fullstack", StringComparison.Ordinal);
        return joined;
    }

    private static List<string> Words(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : NonWord().Split(s.ToLowerInvariant()).Where(w => w.Length > 0).ToList();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.None, 1000)]
    private static partial Regex NonWord();
}

/// <summary>
/// <c>dedupe_key = sha1(norm(company) | norm(title) | location_class)</c> (SDD §8.3). The location class keeps the region
/// (<c>remote-region:india</c>), so one role posted separately for several regions ("Remote - Canada", "Remote - USA")
/// stays several listings and an India posting is never hidden behind a restricted one; Himalayas' country list and
/// Greenhouse's "Remote, India" both classify as <c>remote-region:india</c> and still match.
/// </summary>
public static class DedupeKey
{
    public static string Compute(string company, string title, string locationClass)
    {
        var input = $"{NameNormalizer.Company(company)}|{NameNormalizer.Title(title)}|{locationClass}";
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(input)));
    }
}

/// <summary>One listing sharing a dedupe key, as the canonical choice sees it.</summary>
/// <param name="IsBoard">From an employer ATS board (<c>job_sources.kind = 'board'</c>).</param>
/// <param name="RequireSourceApplyUrl">Its source's <c>require_source_apply_url</c> flag (PRD §7.1a).</param>
/// <param name="Full"><c>description_completeness = 'full'</c>.</param>
public sealed record DedupeCandidate(Guid Id, bool IsBoard, bool RequireSourceApplyUrl, bool Full, DateTimeOffset CreatedAt);

/// <summary>
/// Picks the canonical listing among duplicates (PRD §7.4, §7.1a; SDD §8.3):
/// <list type="number">
/// <item>if an employer ATS record and a record from a <c>require_source_apply_url</c> source (Himalayas, Remote OK…) share
/// the key, the ATS record wins and the aggregator copy is suppressed, whatever their completeness;</item>
/// <item>otherwise the record with the full description wins, then the ATS record;</item>
/// <item>ties go to the oldest listing, so the winner does not flap between runs.</item>
/// </list>
/// Rows are never merged: each keeps its own apply URL and attribution; losers only get <c>duplicate_of_job_id</c>.
/// </summary>
public static class DedupeRanking
{
    public static DedupeCandidate Winner(IReadOnlyCollection<DedupeCandidate> group)
    {
        if (group.Count == 0) throw new ArgumentException("A dedupe group has at least one listing.", nameof(group));
        var boardsFirst = group.Any(c => c.IsBoard) && group.Any(c => !c.IsBoard && c.RequireSourceApplyUrl);
        return group
            .OrderByDescending(c => boardsFirst && c.IsBoard)
            .ThenByDescending(c => c.Full)
            .ThenByDescending(c => c.IsBoard)
            .ThenBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .First();
    }
}
