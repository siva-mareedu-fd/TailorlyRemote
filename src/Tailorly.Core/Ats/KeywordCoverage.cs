using System.Text.RegularExpressions;
using Tailorly.Core.Matching;

namespace Tailorly.Core.Ats;

/// <summary>
/// <c>keyword_coverage</c> (PRD §8.2, deterministic): the job's required (weight 1.0) and preferred (0.5) skills, each
/// with its aliases, looked up in the résumé text case-insensitively with light stemming (plurals). Tech spellings are
/// folded first (<see cref="TermMatcher.Words"/>: <c>C#</c>, <c>.NET</c>, <c>back-end</c>…). A skill matches only inside
/// one line, never across two. One- and two-letter skills ("Go", "R", "C") must appear with the same capitalisation,
/// because in lower case they are ordinary words.
/// </summary>
public static partial class KeywordCoverage
{
    public const double RequiredWeight = 1.0;
    public const double PreferredWeight = 0.5;

    /// <param name="resumeLines">The résumé text, one paragraph (or header/footer line) per entry.</param>
    public static KeywordCoverageResult Compute(JobSkillSet job, IEnumerable<string> resumeLines)
    {
        var index = ResumeIndex.Build(resumeLines);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matched = new List<string>();
        var missing = new List<string>();
        var missingRequired = new List<string>();
        double total = 0, found = 0;

        foreach (var (term, weight) in job.Required.Select(t => (t, RequiredWeight)).Concat(job.Preferred.Select(t => (t, PreferredWeight))))
        {
            var key = Key(term.Skill);
            if (key.Length == 0 || !seen.Add(key)) continue; // a skill listed twice counts once, as required if it is required
            total += weight;
            if (index.Contains(term))
            {
                found += weight;
                matched.Add(term.Skill.Trim());
            }
            else
            {
                missing.Add(term.Skill.Trim());
                if (weight == RequiredWeight) missingRequired.Add(term.Skill.Trim());
            }
        }
        return new KeywordCoverageResult(total == 0 ? null : found / total, matched, missing, missingRequired);
    }

    /// <summary>True when the skill (or one of its aliases) appears in the text.</summary>
    public static bool Mentions(SkillTerm term, IEnumerable<string> lines) => ResumeIndex.Build(lines).Contains(term);

    /// <summary>A comparison key for a skill name: folded, stemmed words joined by spaces (<c>"REST APIs"</c> → <c>"rest api"</c>).</summary>
    public static string Key(string skill) => string.Join(' ', Stemmed(skill));

    /// <summary>Folded words with plurals reduced (<c>APIs</c> → <c>api</c>, <c>libraries</c> → <c>library</c>).</summary>
    public static IReadOnlyList<string> Stemmed(string? text) => TermMatcher.Words(text).Select(Stem).ToList();

    public static string Stem(string word)
    {
        if (word.Length <= 3) return word;
        if (AcronymPlurals.Contains(word)) return word[..^1]; // "apis" is a plural; "redis" and "analysis" are not
        if (word.EndsWith("ies", StringComparison.Ordinal)) return word[..^3] + "y";
        if (word.EndsWith("sses", StringComparison.Ordinal) || word.EndsWith("xes", StringComparison.Ordinal)
            || word.EndsWith("ches", StringComparison.Ordinal) || word.EndsWith("shes", StringComparison.Ordinal))
            return word[..^2];
        if (word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && !word.EndsWith("us", StringComparison.Ordinal)
            && !word.EndsWith("is", StringComparison.Ordinal))
            return word[..^1];
        return word;
    }

    /// <summary>Plurals of acronyms ending in "i" (stemmed to the acronym).</summary>
    private static readonly HashSet<string> AcronymPlurals = new(StringComparer.Ordinal) { "apis", "uis", "kpis", "clis", "guis", "rois", "sdkis" };

    private sealed class ResumeIndex
    {
        private readonly List<string> _lines = []; // " w1 w2 … " per line, folded and stemmed
        private readonly HashSet<string> _rawTokens = new(StringComparer.Ordinal); // original capitalisation

        public static ResumeIndex Build(IEnumerable<string> lines)
        {
            var index = new ResumeIndex();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                index._lines.Add(" " + string.Join(' ', Stemmed(line)) + " ");
                foreach (var token in RawToken().Split(line).Where(t => t.Length > 0))
                    index._rawTokens.Add(token);
            }
            return index;
        }

        public bool Contains(SkillTerm term) => new[] { term.Skill }.Concat(term.Aliases).Any(ContainsSpelling);

        private bool ContainsSpelling(string spelling)
        {
            var words = Stemmed(spelling);
            if (words.Count == 0) return false;
            if (words.Count == 1 && words[0].Length <= 2)
                return _rawTokens.Contains(spelling.Trim());
            var phrase = " " + string.Join(' ', words) + " ";
            return _lines.Any(l => l.Contains(phrase, StringComparison.Ordinal));
        }
    }

    /// <summary>Splits text into tokens for the short-skill check; keeps <c>#</c> and <c>+</c> so "C#" and "C++" stay whole.</summary>
    [GeneratedRegex(@"[^\p{L}\p{Nd}#+]+", RegexOptions.None, 1000)]
    private static partial Regex RawToken();
}
