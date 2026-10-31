using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Tailorly.Docx;

/// <summary>
/// Extracts paragraphs with ids, fingerprints, sections and edit flags (SDD §10.1). Read-only: the file is opened
/// without write access and nothing is saved. Only call on files that passed <see cref="DocxSafetyInspector"/>.
/// </summary>
public static partial class ResumeExtractor
{
    /// <summary>Opens a .docx read-only and extracts it (including layout findings).</summary>
    public static DocModel Extract(Stream docx)
    {
        using var doc = WordprocessingDocument.Open(docx, isEditable: false, new OpenSettings { AutoSave = false });
        return Extract(doc);
    }

    public static DocModel Extract(WordprocessingDocument doc)
    {
        var main = doc.MainDocumentPart ?? throw new InvalidDataException("The document has no main part.");
        var body = main.Document?.Body ?? throw new InvalidDataException("The document has no body.");
        var styles = new StyleMap(main);

        var paragraphs = new List<DocParagraph>();
        var section = ResumeSections.Contact;
        var ordinal = 0;

        foreach (var p in BodyParagraphs(body))
        {
            ordinal++;
            var runs = ReadRuns(p, styles);
            var text = string.Concat(runs.Select(r => r.Text));
            var norm = Normalize(text);
            var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? styles.DefaultParagraphStyle;

            var inTextBox = p.Ancestors<TextBoxContent>().Any() || p.Ancestors().Any(a => a.LocalName == "textbox");
            var inTable = p.Ancestors<TableCell>().Any();
            var complex = IsComplex(p);
            var isBullet = IsBullet(p, styleId, styles, norm);
            // Many résumés use layout tables, so headings are detected in table cells too; text boxes are never headings.
            var heading = !inTextBox ? DetectHeading(norm, runs, styleId, styles, section) : null;
            if (heading is not null) section = heading;

            var flags = Classify(norm, runs, section, heading is not null, inTextBox, inTable, complex, isBullet);
            paragraphs.Add(new DocParagraph(
                Id: "p" + ordinal,
                Fingerprint: Fingerprint(norm),
                Section: section,
                Text: text,
                Runs: runs,
                Flags: flags,
                Style: styles.NameOf(styleId),
                ListLevel: p.ParagraphProperties?.NumberingProperties?.NumberingLevelReference?.Val?.Value));
        }

        var headerFooter = new List<string>();
        foreach (var part in main.HeaderParts.Select(h => (OpenXmlPartRootElement?)h.Header)
                     .Concat(main.FooterParts.Select(f => (OpenXmlPartRootElement?)f.Footer)))
        {
            if (part is null) continue;
            foreach (var p in part.Descendants<Paragraph>().Where(x => !InFallback(x)))
            {
                var t = Normalize(string.Concat(ReadRuns(p, styles).Select(r => r.Text)));
                if (t.Length > 0) headerFooter.Add(t);
            }
        }

        var findings = LayoutAnalyzer.Analyze(doc, paragraphs, headerFooter);
        return new DocModel(paragraphs, headerFooter, findings);
    }

    /// <summary>sha1(normalised text), first 8 hex characters (SDD §10.1).</summary>
    public static string Fingerprint(string normalizedText) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(normalizedText)))[..8];

    /// <summary>Collapses whitespace and trims; used for fingerprints and matching.</summary>
    public static string Normalize(string text) => Whitespace().Replace(text, " ").Trim();

    // ---------- paragraphs and runs (shared with EditApplier, so paragraph ids always mean the same element) ----------

    /// <summary>Body paragraphs in document order: the <c>p{ordinal}</c> ids count these. VML fallback copies are skipped.</summary>
    internal static IEnumerable<Paragraph> BodyParagraphs(Body body) =>
        body.Descendants<Paragraph>().Where(p => !InFallback(p)); // the VML copy of a text box duplicates the DrawingML one

    /// <summary>
    /// The runs of <paramref name="p"/> that carry text, in order, with their text. Runs of a nested paragraph (a text box
    /// inside a drawing) and of a VML fallback copy are skipped, as are runs with no text (a lone bookmark or symbol).
    /// </summary>
    internal static IEnumerable<(Run Run, string Text)> TextRuns(Paragraph p)
    {
        foreach (var r in p.Descendants<Run>())
        {
            if (!ReferenceEquals(r.Ancestors<Paragraph>().FirstOrDefault(), p) || InFallback(r)) continue;
            var text = RunText(r);
            if (text.Length > 0) yield return (r, text);
        }
    }

    /// <summary>A run's text as extraction sees it: tabs and breaks become spaces, a non-breaking hyphen a hyphen.</summary>
    internal static string RunText(Run r)
    {
        var sb = new StringBuilder();
        foreach (var c in r.ChildElements) sb.Append(ChildText(c));
        return sb.ToString();
    }

    internal static string ChildText(OpenXmlElement c) => c switch
    {
        Text t => t.Text,
        TabChar or PositionalTab or Break or CarriageReturn => " ",
        NoBreakHyphen => "-",
        _ => "",
    };

    private static List<DocRun> ReadRuns(Paragraph p, StyleMap styles)
    {
        var pStyle = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? styles.DefaultParagraphStyle;
        var list = new List<DocRun>();
        var index = 0;
        foreach (var (r, text) in TextRuns(p))
        {
            var rpr = r.RunProperties;
            var rStyle = rpr?.RunStyle?.Val?.Value;
            var bold = OnOff(rpr?.Bold) ?? styles.Bold(rStyle) ?? styles.Bold(pStyle) ?? false;
            var italic = OnOff(rpr?.Italic) ?? styles.Italic(rStyle) ?? styles.Italic(pStyle) ?? false;
            var font = rpr?.RunFonts?.Ascii?.Value ?? rpr?.RunFonts?.HighAnsi?.Value ?? styles.Font(rStyle) ?? styles.Font(pStyle);
            var size = StyleMap.ParseHalfPoints(rpr?.FontSize?.Val?.Value) ?? styles.SizePt(rStyle) ?? styles.SizePt(pStyle);
            list.Add(new DocRun(index++, text, bold, italic, font, size) { FormatKey = FormatKey(rpr) });
        }
        return list;
    }

    /// <summary>Run property children that do not change how text looks (language, spell-check, layout hints).</summary>
    private static readonly HashSet<string> InvisibleRunProperties = ["lang", "noProof", "webHidden", "specVanish", "snapToGrid"];

    /// <summary>
    /// The run's own formatting as a comparable string: its run properties without the ones that do not change how text
    /// looks. Two runs with different keys look different (bold word inside a sentence, another font, a colour).
    /// </summary>
    internal static string FormatKey(RunProperties? rpr)
    {
        if (rpr is null) return "";
        var sb = new StringBuilder();
        foreach (var c in rpr.ChildElements)
        {
            if (InvisibleRunProperties.Contains(c.LocalName) || c is RunPropertiesChange) continue;
            if (c is RunFonts f)
            {
                var copy = (RunFonts)f.CloneNode(true);
                copy.Hint = null; // a script hint, not a font choice
                sb.Append(copy.OuterXml);
                continue;
            }
            sb.Append(c.OuterXml);
        }
        return sb.ToString();
    }

    private static bool? OnOff(OnOffType? v) => v is null ? null : v.Val is null || v.Val.Value;

    private static bool InFallback(OpenXmlElement e) => e.Ancestors<AlternateContentFallback>().Any();

    // ---------- classification ----------

    /// <summary>
    /// Hyperlinks, fields, tabs/breaks, bookmarks, content controls, images, symbols (<c>w:sym</c>), tracked changes, notes
    /// and comments make a paragraph complex; complex paragraphs are locked in the MVP (SDD §10.1).
    /// </summary>
    private static bool IsComplex(Paragraph p)
    {
        if (p.Ancestors<SdtBlock>().Any() || p.Ancestors<SdtCell>().Any()) return true;
        foreach (var e in p.Descendants())
        {
            switch (e)
            {
                case Hyperlink or SimpleField or FieldChar or FieldCode or TabChar or PositionalTab or Break or CarriageReturn:
                case SdtRun or Drawing or Picture or EmbeddedObject or SymbolChar:
                case InsertedRun or DeletedRun or MoveFromRun or MoveToRun or Inserted or Deleted:
                case ParagraphPropertiesChange or RunPropertiesChange:
                case FootnoteReference or EndnoteReference or CommentReference or CommentRangeStart:
                case AlternateContent:
                    return true;
                case BookmarkStart b when b.Name?.Value is not "_GoBack":
                    return true;
            }
        }
        return false;
    }

    private static bool IsBullet(Paragraph p, string? styleId, StyleMap styles, string norm) =>
        p.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value is > 0 ||
        styles.IsListStyle(styleId) ||
        (norm.Length > 1 && BulletGlyphs.Contains(norm[0]));

    private const string BulletGlyphs = "•·▪●○■◦‣⁃-–*➢►✓✔";

    private static ParagraphFlags Classify(string norm, List<DocRun> runs, string section, bool isHeading, bool inTextBox,
        bool inTable, bool complex, bool isBullet)
    {
        ParagraphFlags Locked(string reason) => new(false, 0, complex, inTable, inTextBox, isHeading, isBullet, reason);

        if (norm.Length == 0) return Locked("empty");
        if (inTextBox) return Locked("text_box");
        if (isHeading) return Locked("heading");
        if (section == ResumeSections.Contact) return Locked("contact");
        if (complex) return Locked("complex");
        switch (section)
        {
            case ResumeSections.Education: return Locked("education");
            case ResumeSections.Certifications: return Locked("certification");
            case ResumeSections.Other: return Locked("other_section");
        }

        var words = WordCount(norm);
        if (DateRange().IsMatch(norm) || (words <= 8 && MonthYear().IsMatch(norm)))
            return Locked("date_line");

        bool editable;
        switch (section)
        {
            case ResumeSections.Summary:
            case ResumeSections.Skills:
                editable = true;
                break;
            case ResumeSections.Experience:
            case ResumeSections.Projects:
                var boldRatio = runs.Sum(r => r.Bold ? r.Text.Length : 0) / (double)Math.Max(1, runs.Sum(r => r.Text.Length));
                if (!isBullet && ((boldRatio >= 0.8 && words <= 12) || (words <= 6 && !norm.EndsWith('.'))))
                    return Locked("title_line"); // company / job title lines
                editable = isBullet || words >= 8;
                break;
            default:
                editable = false;
                break;
        }
        if (!editable) return Locked("other");
        return new ParagraphFlags(true, LockedPrefix(runs, section), false, inTable, false, false, isBullet, null);
    }

    /// <summary>Leading bold label ("Languages:"), a typed bullet glyph, or in Skills a short "Label:" prefix.</summary>
    private static int LockedPrefix(List<DocRun> runs, string section)
    {
        var total = runs.Sum(r => r.Text.Length);
        var full = string.Concat(runs.Select(r => r.Text));

        var glyph = 0;
        while (glyph < full.Length && (BulletGlyphs.Contains(full[glyph]) || (glyph > 0 && full[glyph] == ' '))) glyph++;
        if (glyph > 0 && (glyph == full.Length || full[..glyph].Trim().Length == 0)) glyph = 0;

        // A bold label right after the glyph (or at the start): "• Pipelines: built…" keeps "• Pipelines: ".
        var boldEnd = -1;
        var pos = 0;
        foreach (var r in runs)
        {
            var start = pos;
            pos += r.Text.Length;
            if (pos <= glyph) continue;
            var part = r.Text[Math.Max(0, glyph - start)..];
            if (r.Bold) boldEnd = pos;
            else if (part.Trim().Length == 0 && boldEnd > 0) boldEnd = pos;
            else break;
        }
        if (boldEnd > glyph && boldEnd < total && full[glyph..boldEnd].Trim().Length > 0 && boldEnd - glyph <= 60)
            return boldEnd;

        if (section == ResumeSections.Skills)
        {
            var colon = full.IndexOf(':', StringComparison.Ordinal);
            if (colon is > 0 and <= 30 && colon < full.Length - 1)
                return Math.Max(colon + 1, glyph);
        }
        return glyph;
    }

    // ---------- headings ----------

    private static readonly (string Section, string[] Phrases)[] HeadingPhrases =
    [
        (ResumeSections.Summary, ["professional summary", "career summary", "executive summary", "profile summary", "summary",
            "professional profile", "career profile", "profile", "about me", "career objective", "objective", "overview",
            "personal statement", "work summary", "experience summary"]),
        (ResumeSections.Experience, ["work experience", "professional experience", "relevant experience", "experience",
            "employment history", "employment details", "employment", "work history", "career history",
            "professional background", "organizational experience", "organisational experience"]),
        (ResumeSections.Skills, ["technical skills", "key skills", "core skills", "professional skills", "skills summary",
            "skills", "core competencies", "competencies", "technologies", "tech stack", "technical expertise",
            "areas of expertise", "expertise", "skill set", "skillset", "technical summary", "it skills"]),
        (ResumeSections.Education, ["educational background", "academic background", "academic qualifications",
            "academic qualification", "educational qualifications", "educational qualification", "education", "academics",
            "qualifications", "qualification"]),
        (ResumeSections.Projects, ["key projects", "personal projects", "selected projects", "side projects", "project experience",
            "project details", "projects", "project"]),
        (ResumeSections.Certifications, ["licenses and certifications", "certifications", "certification", "certificates",
            "certificate", "licenses", "licences", "courses", "training", "trainings"]),
        (ResumeSections.Other, ["awards", "achievements", "honors", "honours", "publications", "languages", "languages known",
            "interests", "hobbies", "references", "volunteering", "volunteer", "activities", "extracurricular",
            "personal details", "personal information", "personal profile", "declaration", "strengths"]),
    ];

    /// <summary>Sections whose unknown sub-headings (role names, "Responsibilities") do not start a new section.</summary>
    private static readonly HashSet<string> SubHeadingSections =
        [ResumeSections.Summary, ResumeSections.Experience, ResumeSections.Projects, ResumeSections.Skills];

    private static string? DetectHeading(string norm, List<DocRun> runs, string? styleId, StyleMap styles, string current)
    {
        if (norm.Length == 0 || norm.Length > 60) return null;
        var key = HeadingKey(norm);
        if (key.Length == 0) return null;
        var words = WordCount(key);
        var styled = styles.IsHeading(styleId);

        foreach (var (section, phrases) in HeadingPhrases)
            if (phrases.Contains(key))
                return section;

        var allBold = runs.Count > 0 && runs.All(r => r.Bold || r.Text.Trim().Length == 0);
        var letters = norm.Where(char.IsLetter).ToArray();
        var allCaps = letters.Length > 1 && letters.All(char.IsUpper);
        if (words <= 5 && (styled || allBold || allCaps))
        {
            // Earliest phrase in the text wins ("Skills & Certifications" → skills).
            string? best = null;
            var bestAt = int.MaxValue;
            foreach (var (section, phrases) in HeadingPhrases)
                foreach (var ph in phrases)
                {
                    var at = (" " + key + " ").IndexOf(" " + ph + " ", StringComparison.Ordinal);
                    if (at >= 0 && at < bestAt) (best, bestAt) = (section, at);
                }
            if (best is not null) return best;
        }
        if (!styled || words > 8) return null;
        // An unknown heading-styled line: a sub-heading inside a known section, otherwise an unknown section.
        return SubHeadingSections.Contains(current) ? current : ResumeSections.Other;
    }

    private static string HeadingKey(string norm)
    {
        var s = norm.ToLowerInvariant().Replace("&", " and ", StringComparison.Ordinal);
        s = NonLetters().Replace(s, " ");
        return Whitespace().Replace(s, " ").Trim();
    }

    private static int WordCount(string s) => s.Length == 0 ? 0 : Whitespace().Split(s.Trim()).Length;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^a-z\s]")]
    private static partial Regex NonLetters();

    private const string Month = @"(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?";
    private const string Point = @"(?:" + Month + @"\s*'?\d{2,4}|\d{1,2}[/.\-]\d{2,4}|(?:19|20)\d{2})";

    [GeneratedRegex(Point + @"\s*(?:-|–|—|to|until|till)\s*(?:" + Point + @"|present|current|now|date|today|ongoing)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DateRange();

    [GeneratedRegex(@"\b" + Month + @"\s*'?\d{2,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthYear();
}
