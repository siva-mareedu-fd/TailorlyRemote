using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;

namespace Tailorly.Docx;

/// <summary>
/// Deterministic layout analysis at upload time (PRD F2, SDD §10.1): tables, columns, text boxes, images,
/// header/footer text, fonts, tracked changes. Informational only; the layout is never restructured.
/// </summary>
public static partial class LayoutAnalyzer
{
    public static FormatFindings Analyze(WordprocessingDocument doc, IReadOnlyList<DocParagraph> paragraphs,
        IReadOnlyList<string> headerFooterLines)
    {
        var main = doc.MainDocumentPart!;
        var body = main.Document!.Body!;
        var roots = new List<OpenXmlElement> { body };
        roots.AddRange(main.HeaderParts.Select(h => (OpenXmlElement?)h.Header).OfType<OpenXmlElement>());
        roots.AddRange(main.FooterParts.Select(f => (OpenXmlElement?)f.Footer).OfType<OpenXmlElement>());

        var hasTables = body.Descendants<Table>().Any();
        var hasColumns = body.Descendants<SectionProperties>().Any(s => s.GetFirstChild<Columns>()?.ColumnCount?.Value > 1);
        var hasTextBoxes = roots.Any(r => r.Descendants<TextBoxContent>().Any() || r.Descendants().Any(e => e.LocalName == "textbox"));
        var hasImages = roots.Any(r => r.Descendants<A.Blip>().Any() || r.Descendants().Any(e => e.LocalName == "imagedata"));
        var tracked = body.Descendants().Any(e => e is InsertedRun or DeletedRun or MoveFromRun or MoveToRun or Inserted or Deleted
            or ParagraphPropertiesChange or RunPropertiesChange or SectionPropertiesChange or TablePropertiesChange);

        var words = paragraphs.Sum(p => CountWords(p.Text));
        var pages = int.TryParse(doc.ExtendedFilePropertiesPart?.Properties?.Pages?.Text, out var pg) && pg > 0
            ? pg
            : Math.Max(1, (int)Math.Ceiling(words / 450.0));

        var hfText = string.Join(" ", headerFooterLines);
        var nonEmpty = paragraphs.Where(p => p.Flags.LockReason != "empty").ToList();

        return new FormatFindings(
            HasTables: hasTables,
            HasColumns: hasColumns,
            HasTextBoxes: hasTextBoxes,
            HasImages: hasImages,
            HeaderFooterText: headerFooterLines.Count > 0,
            HeaderFooterContact: Email().IsMatch(hfText) || Phone().IsMatch(hfText),
            Fonts: CollectFonts(main),
            WordCount: words,
            PageEstimate: pages,
            TrackedChanges: tracked,
            LockedParagraphs: nonEmpty.Count(p => p.Flags.Locked),
            EditableParagraphs: nonEmpty.Count(p => p.Flags.Editable),
            ComplexParagraphs: nonEmpty.Count(p => p.Flags.Complex));
    }

    /// <summary>Fonts named in runs, styles and document defaults; theme references resolve to the theme's latin fonts.</summary>
    private static List<string> CollectFonts(MainDocumentPart main)
    {
        var fonts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var usesMajor = false;
        var usesMinor = false;

        IEnumerable<RunFonts> All()
        {
            foreach (var f in main.Document!.Descendants<RunFonts>()) yield return f;
            if (main.StyleDefinitionsPart?.Styles is { } s)
                foreach (var f in s.Descendants<RunFonts>()) yield return f;
        }

        foreach (var f in All())
        {
            foreach (var name in new[] { f.Ascii?.Value, f.HighAnsi?.Value })
                if (!string.IsNullOrWhiteSpace(name)) fonts.Add(name);
            var theme = f.AsciiTheme?.InnerText + " " + f.HighAnsiTheme?.InnerText;
            if (theme.Contains("major", StringComparison.OrdinalIgnoreCase)) usesMajor = true;
            if (theme.Contains("minor", StringComparison.OrdinalIgnoreCase)) usesMinor = true;
        }

        var scheme = main.ThemePart?.Theme?.ThemeElements?.FontScheme;
        if (usesMajor && scheme?.MajorFont?.LatinFont?.Typeface?.Value is { Length: > 0 } major) fonts.Add(major);
        if ((usesMinor || fonts.Count == 0) && scheme?.MinorFont?.LatinFont?.Typeface?.Value is { Length: > 0 } minor) fonts.Add(minor);
        return [.. fonts];
    }

    private static int CountWords(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [GeneratedRegex(@"[^\s@]+@[^\s@]+\.[a-z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex Email();

    [GeneratedRegex(@"\+?\d[\d\s().-]{8,}\d")]
    private static partial Regex Phone();
}
