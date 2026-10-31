using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tailorly.Docx.Tailoring;

/// <summary>A skill the job names, with its other spellings (from the job's enrichment).</summary>
public sealed record SkillGroup(string Skill, IReadOnlyList<string> Spellings);

/// <summary>
/// What the user has said about themselves: the résumé text, the parsed (and user-corrected) profile and the confirmed
/// skills (PRD §8.3 "original résumé or confirmed facts"). Built once per tailoring; every check compares against it.
/// </summary>
public sealed class FactSource
{
    private readonly HashSet<string> _words = new(StringComparer.Ordinal);
    private readonly List<string> _padded = [];
    private readonly HashSet<(string Value, string Kind)> _numbers = [];
    private readonly HashSet<string> _numberValues = new(StringComparer.Ordinal);
    private readonly string _lower;
    private readonly HashSet<string> _pictographs = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<string> _texts;

    private FactSource(IReadOnlyList<string> texts)
    {
        _texts = texts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        foreach (var t in texts.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            _padded.Add(TechText.Padded(t));
            foreach (var w in TechText.Words(t)) _words.Add(w);
            foreach (var n in FactGuard.Numbers(t))
            {
                _numbers.Add((n.Value, n.Kind));
                _numberValues.Add(n.Value);
            }
            foreach (var p in FactGuard.Pictographs(t)) _pictographs.Add(p);
        }
        _lower = string.Join("\n", texts).ToLowerInvariant();
    }

    /// <param name="texts">Résumé paragraphs and header/footer lines, profile strings, confirmed skills.</param>
    public static FactSource Build(IEnumerable<string> texts) => new(texts.ToList());

    public bool HasWord(string stemmedWord) => _words.Contains(stemmedWord);

    public bool HasPhrase(string phrase) => _padded.Any(p => TechText.ContainsPhrase(p, phrase));

    /// <summary>
    /// A number is known if the same value appears with the same kind (percent, currency, multiplier, scale); a plain
    /// number is also known if the value appears in any form ("3 engineers" backs "three engineers").
    /// </summary>
    public bool HasNumber(NumberToken n) => _numbers.Contains((n.Value, n.Kind)) || (n.Kind == NumberKinds.Plain && _numberValues.Contains(n.Value));

    public bool HasText(string text) => _lower.Contains(text.ToLowerInvariant(), StringComparison.Ordinal);

    /// <summary>
    /// The name appears written as a name: the same characters (ignoring case) as a whole token, in an occurrence that has
    /// a capital letter or a symbol. "Teams" is not backed by "payments team", "React" not by "react to incidents", but
    /// "Javascript" is backed by "JavaScript".
    /// </summary>
    public bool HasName(string name)
    {
        if (name.Length == 0) return false;
        var pattern = new Regex(@"(?<![\p{L}\p{Nd}])" + Regex.Escape(name) + @"(?![\p{L}\p{Nd}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return _texts.Any(t => pattern.Matches(t).Any(m => m.Value.Any(c => char.IsUpper(c) || !char.IsLetter(c))));
    }

    /// <summary>A job skill appears: names (with capitals or symbols) as names, lower-case words as words.</summary>
    public bool HasSkill(string spelling) =>
        spelling.Any(c => char.IsUpper(c) || (!char.IsLetter(c) && c != ' ')) ? HasName(spelling.Trim()) : HasPhrase(spelling);

    public bool HasPictograph(string p) => _pictographs.Contains(p);
}

public static class NumberKinds
{
    public const string Plain = "plain";
}

/// <param name="Value">Canonical value: digits without thousands separators or leading zeros ("1,200" → "1200", "2.50" → "2.5").</param>
/// <param name="Kind"><c>plain</c>, or what qualifies it: <c>%</c>, <c>cur:$</c>, <c>x</c>, <c>scale:m</c> (joined by <c>|</c>).</param>
public sealed record NumberToken(string Value, string Kind, string Raw);

/// <summary>
/// The deterministic truthfulness checks of tailoring (SDD §10.3, PRD §8.3 step 5). Numbers, links, pictographs and new
/// terms in a proposed text are compared with a <see cref="FactSource"/>:
/// <list type="bullet">
/// <item>numbers: every number, percentage, amount, multiplier and spelled-out number must already appear;</item>
/// <item>terms: job skills, capitalised or mixed-case names, terms with digits or tech punctuation, lower-case technology
/// names and credential/seniority words that do not appear are "new terms" (the <c>new_fact</c> flag);</item>
/// <item>links and email addresses must already appear; pictographs (emoji) must already appear.</item>
/// </list>
/// Ordinary lower-case words are allowed: rewording is the point of tailoring.
/// </summary>
public static partial class FactGuard
{
    // ---------- numbers ----------

    private static readonly Dictionary<string, int> SpelledNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
        ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30,
        ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
        ["hundred"] = 100, ["dozen"] = 12, ["dozens"] = 12, ["hundreds"] = 100,
    };

    private static readonly Dictionary<string, string> Scales = new(StringComparer.OrdinalIgnoreCase)
    {
        ["k"] = "k", ["thousand"] = "k", ["m"] = "m", ["mn"] = "m", ["million"] = "m", ["b"] = "b", ["bn"] = "b",
        ["billion"] = "b", ["lakh"] = "lakh", ["lakhs"] = "lakh", ["crore"] = "crore", ["crores"] = "crore", ["cr"] = "crore",
    };

    /// <summary>
    /// Numbers in <paramref name="text"/>. Digits glued to letters are names, not numbers ("OAuth2", "EC2", "k8s", "3D",
    /// "2nd"); the term check handles those. "One" is not counted (it is mostly not a quantity).
    /// </summary>
    public static IReadOnlyList<NumberToken> Numbers(string text)
    {
        var list = new List<NumberToken>();
        foreach (Match m in Digits().Matches(text))
        {
            var start = m.Index;
            var end = m.Index + m.Length;
            if (start > 0 && char.IsLetter(text[start - 1])) continue;

            var after = text[end..];
            var kinds = new List<string>();
            var suffix = Suffix().Match(after);
            if (suffix.Success)
            {
                var s = suffix.Groups["s"].Value;
                if (s is "%" || s.Equals("percent", StringComparison.OrdinalIgnoreCase)) kinds.Add("%");
                else if (s.Equals("x", StringComparison.OrdinalIgnoreCase)) kinds.Add("x");
                else if (Scales.TryGetValue(s, out var scale)) kinds.Add("scale:" + scale);
            }
            else if (after.Length > 0 && char.IsLetter(after[0]))
            {
                continue; // "3D", "2FA", "1st": a name or an ordinal, not an amount
            }
            if (Currency().Match(text[..start]) is { Success: true } cur) kinds.Insert(0, "cur:" + CurrencyKey(cur.Groups["c"].Value));

            list.Add(new NumberToken(Canonical(m.Value), kinds.Count == 0 ? NumberKinds.Plain : string.Join('|', kinds), m.Value));
        }
        foreach (Match m in Spelled().Matches(text))
        {
            var unit = m.Groups["unit"].Value;
            var value = m.Groups["tens"].Success
                ? SpelledNumbers[m.Groups["tens"].Value] + (unit.Equals("one", StringComparison.OrdinalIgnoreCase) ? 1 : SpelledNumbers[unit])
                : SpelledNumbers[m.Groups["word"].Value];
            list.Add(new NumberToken(value.ToString(CultureInfo.InvariantCulture), NumberKinds.Plain, m.Value));
        }
        return list;
    }

    /// <summary>Numbers in <paramref name="text"/> that <paramref name="source"/> does not contain.</summary>
    public static IReadOnlyList<NumberToken> NewNumbers(string text, FactSource source) =>
        Numbers(text).Where(n => !source.HasNumber(n)).ToList();

    private static string Canonical(string digits)
    {
        // A comma followed by exactly three digits separates thousands; anything else is a decimal point.
        var sb = new StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            var c = digits[i];
            if (c == ',' && i + 4 <= digits.Length && digits.Skip(i + 1).Take(3).All(char.IsDigit)
                && (i + 4 == digits.Length || !char.IsDigit(digits[i + 4])))
                continue;
            sb.Append(c == ',' ? '.' : c);
        }
        var s = sb.ToString();
        var dot = s.IndexOf('.');
        var whole = (dot < 0 ? s : s[..dot]).TrimStart('0');
        if (whole.Length == 0) whole = "0";
        if (dot < 0) return whole;
        var frac = s[(dot + 1)..].Replace(".", "", StringComparison.Ordinal).TrimEnd('0');
        return frac.Length == 0 ? whole : whole + "." + frac;
    }

    private static string CurrencyKey(string c) => c.Trim().TrimEnd('.').ToLowerInvariant() switch
    {
        "$" or "usd" => "usd",
        "₹" or "inr" or "rs" => "inr",
        "€" or "eur" => "eur",
        "£" or "gbp" => "gbp",
        var other => other,
    };

    // ---------- links, pictographs, markdown, first person ----------

    public static IReadOnlyList<string> Links(string text) =>
        Link().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ';', ':', ')', ']')).ToList();

    /// <summary>Emoji and other pictographs (supplementary-plane symbols, dingbats, misc symbols, variation selectors).</summary>
    public static IReadOnlyList<string> Pictographs(string text)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            if (char.IsSurrogate(element[0]) && (element.Length < 2 || !char.IsSurrogatePair(element[0], element[1])))
            {
                list.Add(element); // a broken surrogate is never résumé text
                continue;
            }
            var cp = char.ConvertToUtf32(element, 0);
            if (cp is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or >= 0x2B00 and <= 0x2BFF
                || element.Contains('️', StringComparison.Ordinal))
                list.Add(element);
        }
        return list;
    }

    public static bool HasMarkdown(string text) => Markdown().IsMatch(text);

    public static bool HasFirstPerson(string text) => FirstPerson().IsMatch(text);

    // ---------- terms ----------

    /// <summary>
    /// Terms in <paramref name="text"/> that <paramref name="source"/> does not contain, in order of appearance: job skills
    /// first (in the job's spelling), then other fact-like tokens; adjacent new tokens are joined ("Google Cloud").
    /// </summary>
    /// <param name="listLike">The text is a list of names (a Skills line): a capitalised word is a name even at the start.</param>
    public static IReadOnlyList<string> NewTerms(string text, FactSource source, IReadOnlyList<SkillGroup> jobSkills, bool listLike = false)
    {
        var found = new List<string>();
        var padded = TechText.Padded(text);
        var knownWords = new HashSet<string>(StringComparer.Ordinal);            // every spelling of a job skill the user has
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.Ordinal);              // words of job skills already reported
        foreach (var group in jobSkills)
        {
            var spellings = group.Spellings.Prepend(group.Skill).Where(s => TechText.Words(s).Count > 0).ToList();
            if (spellings.Any(source.HasSkill))
            {
                foreach (var s in spellings)
                {
                    knownNames.Add(s.Trim());
                    foreach (var w in TechText.Words(s)) knownWords.Add(w);
                }
                continue;
            }
            var used = spellings.Where(s => TechText.ContainsPhrase(padded, s)).ToList();
            if (used.Count == 0) continue;
            found.Add(group.Skill.Trim());
            foreach (var w in used.SelectMany(TechText.Words)) reported.Add(w);
        }

        var tokens = Tokens(text);
        string? run = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var (token, sentenceStart) = tokens[i];
            var words = TechText.Words(token);
            var isNew = words.Count > 0 && !words.All(reported.Contains) && !IsKnown(token, words, sentenceStart && !listLike)
                        && IsFactLike(token, words, sentenceStart && !listLike);
            if (isNew)
            {
                run = run is null ? token : run + " " + token;
                continue;
            }
            if (run is not null) found.Add(run);
            run = null;
        }
        if (run is not null) found.Add(run);
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // A name (a capital beyond a plain sentence start, or a symbol) must appear as a name; a lower-case word as a word.
        bool IsKnown(string token, IReadOnlyList<string> words, bool sentenceStart)
        {
            var name = Possessive().Replace(token, "");
            if (name.Length == 0) return true;
            if (knownNames.Contains(name) || words.All(knownWords.Contains)) return true;
            var nameLike = name.Skip(1).Any(char.IsUpper) || (char.IsUpper(name[0]) && !sentenceStart);
            if (!nameLike) return words.All(source.HasWord);
            return source.HasName(name) || (name.EndsWith('s') && name.Length > 2 && source.HasName(name[..^1]))
                   || source.HasName(name + "s");
        }
    }

    private static bool IsFactLike(string token, IReadOnlyList<string> words, bool sentenceStart)
    {
        if (TechText.Abbreviations.Contains(token)) return false;
        if (words.Any(w => TechText.Lexicon.Contains(w) || TechText.FactWords.Contains(w))) return true;
        if (token.Length < 2) return false;
        var letters = token.Count(char.IsLetter);
        if (letters == 0) return false;
        if (token.Skip(1).Any(char.IsUpper)) return true;                       // PostgreSQL, iOS, AWS, McKinsey
        if (char.IsUpper(token[0]) && !sentenceStart) return true;              // a name in mid-sentence
        if (token.Any(char.IsDigit)) return true;                               // OAuth2, EC2, 3D
        var inner = token[1..^1];
        return inner.IndexOfAny(['#', '+', '/', '.']) >= 0 || token.EndsWith('#') || token.EndsWith("++", StringComparison.Ordinal);
    }

    /// <summary>Whitespace-separated tokens without surrounding quotes, brackets and trailing punctuation, with sentence starts.</summary>
    internal static List<(string Token, bool SentenceStart)> Tokens(string text)
    {
        var list = new List<(string, bool)>();
        var start = true;
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim('"', '\'', '(', ')', '[', ']', '{', '}', '“', '”', '‘', '’', '«', '»');
            // A colon or semicolon does not end a sentence: in a résumé what follows is usually a list of names.
            var endsSentence = t.Length > 0 && t[^1] is '.' or '!' or '?';
            t = t.TrimEnd('.', ',', ';', ':', '!', '?');
            if (t.Length == 0 || t is "-" or "–" or "—" or "•" or "|" or "/")
            {
                start = true;
                continue;
            }
            list.Add((t, start));
            start = endsSentence;
        }
        return list;
    }

    [GeneratedRegex(@"\d+(?:[.,]\d+)*", RegexOptions.None, 1000)]
    private static partial Regex Digits();

    [GeneratedRegex(@"['’]s$", RegexOptions.None, 1000)]
    private static partial Regex Possessive();

    [GeneratedRegex(@"^(?:\s?(?<s>%|percent\b|million\b|billion\b|thousand\b|lakhs?\b|crores?\b)|(?<s>[xX]|[kKmMbB]|mn|bn|cr)(?![\p{L}\p{Nd}]))",
        RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Suffix();

    [GeneratedRegex(@"(?<c>[$€£₹]|\b(?:usd|inr|eur|gbp|rs\.?))\s?$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Currency();

    [GeneratedRegex(@"\b(?:(?<tens>twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)-(?<unit>one|two|three|four|five|six|seven|eight|nine)|(?<word>two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundreds?|dozens?))\b",
        RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Spelled();

    [GeneratedRegex(@"https?://\S+|\bwww\.\S+|[\w.+-]+@[\w-]+(?:\.[\w-]+)+", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Link();

    [GeneratedRegex(@"\*\*|__|`|~~|^\s*(?:#{1,6}\s|>\s|[-*+•]\s)|\[[^\]]+\]\([^)]*\)", RegexOptions.Multiline, 1000)]
    private static partial Regex Markdown();

    /// <summary>"I" (also in "I'm"), and me/my/mine/myself/we/our/ours in either case; "us" only in lower case ("US" is a country).</summary>
    [GeneratedRegex(@"\bI\b|\b(?:[Mm]e|[Mm]y|[Mm]ine|[Mm]yself|[Ww]e|[Oo]ur|[Oo]urs|us)\b", RegexOptions.None, 1000)]
    private static partial Regex FirstPerson();
}
