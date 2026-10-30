using System.Text.RegularExpressions;

namespace Tailorly.Core.Matching;

/// <summary>
/// Cheap, deterministic matching of target roles and skills against job titles and text, for the raw prefilter and the
/// rule-based fallback ranking. Tech spellings are folded first (<c>C#</c> → <c>csharp</c>, <c>.NET</c> → <c>dotnet</c>,
/// <c>back-end</c> → <c>backend</c>), then text is compared as words.
/// </summary>
public static partial class TermMatcher
{
    /// <summary>Words that say seniority or "engineer", not what the job is about.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "engineer", "engineering", "developer", "development", "dev", "eng", "programmer", "software", "senior", "sr", "junior",
        "jr", "lead", "staff", "principal", "head", "i", "ii", "iii", "iv", "1", "2", "3", "4", "the", "and", "of", "for", "in",
        "a", "an", "specialist", "associate", "consultant", "expert", "professional", "remote", "mid", "level",
    };

    private static readonly (Regex Pattern, string Replacement)[] Folds =
    [
        (FoldCSharp(), " csharp "),
        (FoldCpp(), " cpp "),
        (FoldDotnet(), " dotnet "),
        (FoldNode(), " nodejs "),
        (FoldBackend(), " backend "),
        (FoldFrontend(), " frontend "),
        (FoldFullstack(), " fullstack "),
        (FoldSre(), " sre "),
        (FoldMl(), " ml "),
    ];

    /// <summary>Lower-cased words after folding tech spellings.</summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var t = text.Length > 4000 ? text[..4000] : text;
        t = t.ToLowerInvariant();
        foreach (var (pattern, replacement) in Folds) t = pattern.Replace(t, replacement);
        return NonWord().Split(t).Where(w => w.Length > 0).ToList();
    }

    /// <summary>
    /// How well a title fits the best target role, 0–1: the share of the role's meaningful words found in the title.
    /// A role made only of generic words ("Software Engineer") matches any engineer/developer title.
    /// </summary>
    public static double RoleScore(string title, IReadOnlyList<string> roles)
    {
        if (roles.Count == 0) return 0;
        var titleWords = Words(title).ToHashSet(StringComparer.Ordinal);
        double best = 0;
        foreach (var role in roles)
        {
            var words = Words(role).Distinct(StringComparer.Ordinal).ToList();
            var meaningful = words.Where(w => !Generic.Contains(w)).ToList();
            double score;
            if (meaningful.Count == 0)
                score = words.Any(titleWords.Contains) || titleWords.Overlaps(["engineer", "developer"]) ? 1 : 0;
            else
                score = meaningful.Count(titleWords.Contains) / (double)meaningful.Count;
            best = Math.Max(best, score);
        }
        return best;
    }

    /// <summary>
    /// Distinct skills found in <paramref name="text"/> as whole words (after folding). Skills of one or two letters
    /// ("Go", "R") are only counted when <paramref name="allowShort"/> is set (titles), because in prose they are
    /// ordinary words.
    /// </summary>
    public static int SkillHits(string? text, IReadOnlyList<string> skills, bool allowShort)
    {
        if (string.IsNullOrWhiteSpace(text) || skills.Count == 0) return 0;
        var words = Words(text);
        var set = words.ToHashSet(StringComparer.Ordinal);
        var joined = " " + string.Join(' ', words) + " ";
        var hits = 0;
        foreach (var skill in skills.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sw = Words(skill);
            if (sw.Count == 0) continue;
            if (sw.Count == 1)
            {
                if (sw[0].Length <= 2 && !allowShort) continue;
                if (set.Contains(sw[0])) hits++;
            }
            else if (joined.Contains(" " + string.Join(' ', sw) + " ", StringComparison.Ordinal))
            {
                hits++;
            }
        }
        return hits;
    }

    /// <summary>True when any of <paramref name="phrases"/> appears in <paramref name="text"/> as whole words.</summary>
    public static bool ContainsAny(string? text, IReadOnlyList<string> phrases)
    {
        if (string.IsNullOrWhiteSpace(text) || phrases.Count == 0) return false;
        var joined = " " + string.Join(' ', Words(text)) + " ";
        return phrases.Select(p => Words(p)).Where(w => w.Count > 0)
            .Any(w => joined.Contains(" " + string.Join(' ', w) + " ", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"(?<![a-z0-9])c\s?#|\bc-sharp\b", RegexOptions.None, 1000)]
    private static partial Regex FoldCSharp();

    [GeneratedRegex(@"(?<![a-z0-9])c\+\+", RegexOptions.None, 1000)]
    private static partial Regex FoldCpp();

    [GeneratedRegex(@"(?<![a-z0-9])\.net\b|\bdot\s?net\b", RegexOptions.None, 1000)]
    private static partial Regex FoldDotnet();

    [GeneratedRegex(@"\bnode(\.js|js)?\b", RegexOptions.None, 1000)]
    private static partial Regex FoldNode();

    [GeneratedRegex(@"\bback[\s-]?end\b", RegexOptions.None, 1000)]
    private static partial Regex FoldBackend();

    [GeneratedRegex(@"\bfront[\s-]?end\b", RegexOptions.None, 1000)]
    private static partial Regex FoldFrontend();

    [GeneratedRegex(@"\bfull[\s-]?stack\b", RegexOptions.None, 1000)]
    private static partial Regex FoldFullstack();

    [GeneratedRegex(@"\bsite reliability( engineering)?\b", RegexOptions.None, 1000)]
    private static partial Regex FoldSre();

    [GeneratedRegex(@"\bmachine learning\b", RegexOptions.None, 1000)]
    private static partial Regex FoldMl();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.None, 1000)]
    private static partial Regex NonWord();
}
