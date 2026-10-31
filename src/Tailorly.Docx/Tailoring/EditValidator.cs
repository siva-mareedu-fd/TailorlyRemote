using System.Text;
using System.Text.RegularExpressions;

namespace Tailorly.Docx.Tailoring;

/// <summary>Edit flags shown to the user (ApiDesign §4.8).</summary>
public static class EditFlags
{
    /// <summary>Adds a term (skill, name, credential) that is not in the résumé, profile or confirmed skills; rejected by default.</summary>
    public const string NewFact = "new_fact";

    /// <summary>Longer than <see cref="TailorRules.MaxLengthRatio"/> × the original paragraph.</summary>
    public const string Length = "length";

    /// <summary>The paragraph has mixed inline formatting that accepting would flatten to the dominant run's (SDD §10.2).</summary>
    public const string FormattingSimplified = "formatting_simplified";
}

/// <param name="MaxLengthRatio"><c>Tailor:MaxLengthRatio</c> (PRD Appendix A, default 1.15).</param>
/// <param name="MaxEdits">Edit cap per tailoring, so the diff stays reviewable (SDD §10.3, default 25).</param>
/// <param name="AllowFirstPerson">Allow I/my/we in edits even where the paragraph has none ("résumé norms", SDD §10.3).</param>
public sealed record TailorRules(double MaxLengthRatio = 1.15, int MaxEdits = 25, bool AllowFirstPerson = false);

/// <summary>One edit as the model proposed it (PRD Appendix B, <c>resume_tailor</c>).</summary>
public sealed record ProposedEdit(string ParagraphId, string NewText, string? Rationale, bool UsesConfirmedFact);

/// <summary>
/// An edit that passed validation, ready for the user's decision. <see cref="Proposed"/> is the whole new paragraph text,
/// starting with the paragraph's locked prefix exactly as it was.
/// </summary>
/// <param name="NewTerms">The terms behind a <see cref="EditFlags.NewFact"/> flag (empty otherwise).</param>
public sealed record CheckedEdit(
    string Id,
    string ParagraphId,
    string Fingerprint,
    string Section,
    string Original,
    string Proposed,
    string Rationale,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> NewTerms);

/// <summary>Everything validation compares an edit with: the extracted résumé, the user's facts, the job's skills, the limits.</summary>
public sealed class TailorContext
{
    public TailorContext(DocModel model, FactSource facts, IReadOnlyList<SkillGroup> jobSkills, TailorRules rules)
    {
        Model = model;
        Facts = facts;
        JobSkills = jobSkills;
        Rules = rules;
        Paragraphs = model.Paragraphs.ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    public DocModel Model { get; }
    public FactSource Facts { get; }
    public IReadOnlyList<SkillGroup> JobSkills { get; }
    public TailorRules Rules { get; }
    public IReadOnlyDictionary<string, DocParagraph> Paragraphs { get; }
}

/// <summary>
/// Deterministic validation of <c>resume_tailor</c> edits (SDD §10.3, PRD §8.3 step 5), in two tiers:
/// <list type="number">
/// <item><see cref="Violations"/>: breaks of the edit-only contract (unknown or locked paragraph, duplicate, over the cap,
/// empty, line break, markdown, emoji, a link, first person) and invented numbers. They go back to the model in the one
/// repair turn; if they persist the task fails cleanly (CLAUDE.md hard rule).</item>
/// <item><see cref="Check"/>: per-edit flags for the user: <c>new_fact</c> (a term the user never stated; rejected by
/// default), <c>length</c> and <c>formatting_simplified</c>.</item>
/// </list>
/// Messages carry paragraph ids and positions only, never résumé text, so they are safe to count in logs.
/// </summary>
public static partial class EditValidator
{
    public static IReadOnlyList<string> Violations(IReadOnlyList<ProposedEdit> edits, TailorContext ctx)
    {
        var errors = new List<string>();
        if (edits.Count > ctx.Rules.MaxEdits)
            errors.Add($"Return at most {ctx.Rules.MaxEdits} edits (there are {edits.Count}); keep the most useful ones.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            var id = SafeId(e.ParagraphId);
            var at = $"edits[{i}] ({id})";
            if (!ctx.Paragraphs.TryGetValue(e.ParagraphId ?? "", out var p))
            {
                errors.Add($"{at} names a paragraph that does not exist; use only ids from the résumé data.");
                continue;
            }
            if (!p.Flags.Editable)
            {
                errors.Add($"{at} targets a locked paragraph; only paragraphs marked editable may change.");
                continue;
            }
            if (!seen.Add(p.Id)) errors.Add($"{at} edits the same paragraph again; give at most one edit per paragraph.");

            var raw = e.NewText ?? "";
            if (raw.Contains('\n') || raw.Contains('\r'))
                errors.Add($"{at} contains a line break; a paragraph must stay one paragraph.");
            var text = EditablePart(p, raw);
            if (text.Length == 0)
            {
                errors.Add($"{at} has an empty new_text.");
                continue;
            }
            if (FactGuard.HasMarkdown(text)) errors.Add($"{at} uses markdown or a bullet character; return plain text.");
            if (FactGuard.Pictographs(text).Any(x => !ctx.Facts.HasPictograph(x))) errors.Add($"{at} contains an emoji or pictograph.");
            if (FactGuard.Links(text).Any(l => !ctx.Facts.HasText(l)))
                errors.Add($"{at} adds a link or email address that is not in the résumé.");
            if (!ctx.Rules.AllowFirstPerson && FactGuard.HasFirstPerson(text) && !FactGuard.HasFirstPerson(p.Text))
                errors.Add($"{at} uses first-person words (I, my, we); keep the paragraph's own style.");
            if (FactGuard.NewNumbers(text, ctx.Facts).Count > 0)
                errors.Add($"{at} adds a number that does not appear in the résumé; use only numbers the résumé states, or drop the claim.");
        }
        return errors;
    }

    /// <summary>
    /// Validated edits with their flags and the default decision basis. Edits that change nothing are dropped. Call only
    /// after <see cref="Violations"/> returned none.
    /// </summary>
    public static IReadOnlyList<CheckedEdit> Check(IReadOnlyList<ProposedEdit> edits, TailorContext ctx)
    {
        var result = new List<CheckedEdit>();
        foreach (var e in edits)
        {
            var p = ctx.Paragraphs[e.ParagraphId];
            var proposed = ProposedText(p, e.NewText);
            if (ResumeExtractor.Normalize(proposed) == ResumeExtractor.Normalize(p.Text)) continue;

            var editable = proposed[p.Flags.LockedPrefixLength..];
            var terms = FactGuard.NewTerms(editable, ctx.Facts, ctx.JobSkills, listLike: p.Section == ResumeSections.Skills);
            var flags = new List<string>();
            if (terms.Count > 0) flags.Add(EditFlags.NewFact);
            if (ResumeExtractor.Normalize(proposed).Length > ResumeExtractor.Normalize(p.Text).Length * ctx.Rules.MaxLengthRatio)
                flags.Add(EditFlags.Length);
            if (FormattingSimplified(p)) flags.Add(EditFlags.FormattingSimplified);

            result.Add(new CheckedEdit("e" + (result.Count + 1), p.Id, p.Fingerprint, p.Section, ResumeExtractor.Normalize(p.Text),
                proposed, Clean(e.Rationale ?? "", 300), flags, terms));
        }
        return result;
    }

    /// <summary>
    /// The whole new paragraph: the locked prefix exactly as in the résumé, the original's leading whitespace after it,
    /// then the model's text (whitespace collapsed, control characters removed, a repeated prefix dropped).
    /// </summary>
    public static string ProposedText(DocParagraph p, string newText)
    {
        var prefix = p.Text[..p.Flags.LockedPrefixLength];
        var rest = p.Text[p.Flags.LockedPrefixLength..];
        var lead = rest[..(rest.Length - rest.TrimStart().Length)];
        return prefix + lead + EditablePart(p, newText);
    }

    /// <summary>The model's text for the part after the prefix, normalised.</summary>
    internal static string EditablePart(DocParagraph p, string newText)
    {
        var t = Clean(newText, int.MaxValue);
        var prefix = p.Text[..p.Flags.LockedPrefixLength].Trim();
        if (prefix.Length > 0 && t.StartsWith(prefix, StringComparison.Ordinal)) t = t[prefix.Length..].TrimStart();
        return t;
    }

    /// <summary>
    /// True when the editable part of the paragraph (after the prefix) has runs that look different (a bold word in a
    /// sentence, another font): accepting puts all new text in the dominant run's formatting.
    /// </summary>
    public static bool FormattingSimplified(DocParagraph p)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var pos = 0;
        foreach (var r in p.Runs)
        {
            var start = pos;
            pos += r.Text.Length;
            if (pos <= p.Flags.LockedPrefixLength) continue;
            var part = r.Text[Math.Max(0, p.Flags.LockedPrefixLength - start)..];
            if (part.Trim().Length > 0) keys.Add(r.FormatKey);
        }
        return keys.Count > 1;
    }

    /// <summary>Collapses whitespace, drops control characters (also invalid in XML), trims, caps the length.</summary>
    internal static string Clean(string s, int max)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) continue;
            if (c is '￾' or '￿') continue;
            sb.Append(c);
        }
        var t = Whitespace().Replace(sb.ToString(), " ").Trim();
        return t.Length <= max ? t : t[..max].TrimEnd();
    }

    private static string SafeId(string? id)
    {
        var s = new string((id ?? "").Where(char.IsLetterOrDigit).Take(12).ToArray());
        return s.Length == 0 ? "?" : s;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
