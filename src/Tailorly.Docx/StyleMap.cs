using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Tailorly.Docx;

/// <summary>Resolves the few style properties extraction needs (heading, bold, list, font), following <c>basedOn</c>.</summary>
internal sealed partial class StyleMap
{
    private sealed record Entry(string Id, string? Name, string? BasedOn, bool? Bold, bool? Italic, string? Font, double? SizePt,
        int? OutlineLevel, bool HasNumbering);

    private readonly Dictionary<string, Entry> _styles = new(StringComparer.Ordinal);
    private readonly string? _defaultParagraphStyle;

    public StyleMap(MainDocumentPart main)
    {
        var styles = main.StyleDefinitionsPart?.Styles;
        if (styles is null) return;
        foreach (var s in styles.Elements<Style>())
        {
            var id = s.StyleId?.Value;
            if (id is null) continue;
            var rpr = s.StyleRunProperties;
            var ppr = s.StyleParagraphProperties;
            _styles[id] = new Entry(
                id,
                s.StyleName?.Val?.Value,
                s.BasedOn?.Val?.Value,
                rpr?.Bold is { } b ? b.Val is null || b.Val.Value : null,
                rpr?.Italic is { } i ? i.Val is null || i.Val.Value : null,
                rpr?.RunFonts?.Ascii?.Value ?? rpr?.RunFonts?.HighAnsi?.Value,
                ParseHalfPoints(rpr?.FontSize?.Val?.Value),
                ppr?.OutlineLevel?.Val?.Value,
                ppr?.NumberingProperties?.NumberingId?.Val?.Value is > 0);
            if (s.Type?.Value == StyleValues.Paragraph && s.Default?.Value == true)
                _defaultParagraphStyle = id;
        }
    }

    public string? DefaultParagraphStyle => _defaultParagraphStyle;

    public string? NameOf(string? styleId) => styleId is not null && _styles.TryGetValue(styleId, out var e) ? e.Name ?? e.Id : styleId;

    /// <summary>Heading 1–9 (by name or outline level 0–8). Title and Subtitle are not section headings.</summary>
    public bool IsHeading(string? styleId) => Walk(styleId, e =>
        e.Name is { } n && HeadingName().IsMatch(n) ? true :
        e.OutlineLevel is >= 0 and <= 8 ? true : (bool?)null) ?? false;

    public bool IsListStyle(string? styleId) =>
        Walk(styleId, e => e.HasNumbering || (e.Name?.Contains("List", StringComparison.OrdinalIgnoreCase) ?? false) ? true : (bool?)null) ?? false;

    public bool? Bold(string? styleId) => Walk(styleId, e => e.Bold);
    public bool? Italic(string? styleId) => Walk(styleId, e => e.Italic);
    public string? Font(string? styleId) => Walk(styleId, e => e.Font);
    public double? SizePt(string? styleId) => Walk(styleId, e => e.SizePt);

    private T? Walk<T>(string? styleId, Func<Entry, T?> pick)
    {
        var seen = 0;
        while (styleId is not null && seen++ < 20 && _styles.TryGetValue(styleId, out var e))
        {
            var v = pick(e);
            if (v is not null) return v;
            styleId = e.BasedOn;
        }
        return default;
    }

    internal static double? ParseHalfPoints(string? v) => int.TryParse(v, out var half) ? half / 2.0 : null;

    [GeneratedRegex(@"^heading\s*\d$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingName();
}
