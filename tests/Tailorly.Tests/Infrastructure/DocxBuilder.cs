using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using WPS = DocumentFormat.OpenXml.Office2010.Word.DrawingShape;

namespace Tailorly.Tests.Infrastructure;

/// <summary>
/// Builds synthetic résumé .docx files with the OpenXML SDK (no real résumés in the repo). Each method appends one
/// paragraph or feature; <see cref="Build"/> returns the file bytes.
/// </summary>
public sealed class DocxBuilder
{
    private readonly List<Action<Body, MainDocumentPart>> _steps = [];
    private readonly List<string> _headerLines = [];
    private int _columns = 1;
    private bool _writeProtected;

    public static byte[] SampleResume(string summary = "Backend engineer with 6 years of experience building .NET services for payments.") =>
        new DocxBuilder()
            .Title("Asha Verma")
            .Para("Hyderabad, India · asha@example.test · +91 90000 00000")
            .Heading("Summary")
            .Para(summary)
            .Heading("Experience")
            .Para("Senior Software Engineer, Example Corp", bold: true)
            .Para("Jan 2021 – Present")
            .Bullet("Built REST APIs for the payments team using C# and PostgreSQL.")
            .Bullet("Reduced settlement latency by 30% by redesigning the nightly batch pipeline.")
            .Para("Software Engineer, Sample Systems", bold: true)
            .Para("Jul 2018 – Dec 2020")
            .Bullet("Maintained billing services and wrote integration tests for every release.")
            .Heading("Skills")
            .Label("Languages: ", "C#, SQL, TypeScript")
            .Label("Platforms: ", ".NET, PostgreSQL, Docker")
            .Heading("Education")
            .Para("B.Tech, Computer Science, Example University, 2018")
            .Heading("Certifications")
            .Para("Microsoft Certified: Azure Developer Associate")
            .Build();

    public DocxBuilder Title(string text) => Add(new Paragraph(Style("Title"), Run(text)));
    public DocxBuilder Heading(string text) => Add(new Paragraph(Style("Heading1"), Run(text)));
    public DocxBuilder Para(string text, bool bold = false, string? font = null) => Add(new Paragraph(Run(text, bold, font)));

    public DocxBuilder Bullet(string text) => Add(new Paragraph(
        new ParagraphProperties(new ParagraphStyleId { Val = "ListParagraph" },
            new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })),
        Run(text)));

    /// <summary>A paragraph that starts with a bold label (e.g. "Languages: ").</summary>
    public DocxBuilder Label(string boldLabel, string rest) => Add(new Paragraph(Run(boldLabel, bold: true), Run(rest)));

    public DocxBuilder WithTab(string left, string right) => Add(new Paragraph(new Run(new Text(left), new TabChar(), new Text(right))));

    public DocxBuilder Hyperlink(string text, string url)
    {
        _steps.Add((body, main) =>
        {
            var rel = main.AddHyperlinkRelationship(new Uri(url), isExternal: true);
            body.AppendChild(new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Hyperlink(Run(text)) { Id = rel.Id }));
        });
        return this;
    }

    public DocxBuilder TableRow(params string[] cells)
    {
        _steps.Add((body, _) => body.AppendChild(new Table(
            new TableProperties(new TableWidth { Type = TableWidthUnitValues.Auto, Width = "0" }),
            new TableRow(cells.Select(c => new TableCell(new Paragraph(Run(c))))))));
        return this;
    }

    /// <summary>A floating text box (DrawingML wps shape with a VML fallback, as Word writes it).</summary>
    public DocxBuilder TextBox(string text)
    {
        _steps.Add((body, _) =>
        {
            var shape = new WPS.WordprocessingShape(
                new WPS.NonVisualDrawingShapeProperties { TextBox = true },
                new WPS.ShapeProperties(),
                new WPS.TextBoxInfo2(new TextBoxContent(new Paragraph(Run(text)))),
                new WPS.TextBodyProperties());
            var graphic = new A.Graphic(new A.GraphicData(shape)
                { Uri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape" });
            var anchor = new DW.Inline(new DW.Extent { Cx = 1828800, Cy = 457200 },
                new DW.DocProperties { Id = 10U, Name = "Text Box 1" }, graphic);
            var fallback = new AlternateContentFallback(new Picture(
                new DocumentFormat.OpenXml.Vml.Shape(new DocumentFormat.OpenXml.Vml.TextBox(new TextBoxContent(new Paragraph(Run(text)))))));
            var choice = new AlternateContentChoice(new Drawing(anchor)) { Requires = "wps" };
            body.AppendChild(new Paragraph(new Run(new AlternateContent(choice, fallback))));
        });
        return this;
    }

    public DocxBuilder Image()
    {
        _steps.Add((body, main) =>
        {
            var part = main.AddImagePart(ImagePartType.Png);
            using (var ms = new MemoryStream(OnePixelPng)) part.FeedData(ms);
            var relId = main.GetIdOfPart(part);
            var pic = new PIC.Picture(
                new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = 0U, Name = "photo.png" },
                    new PIC.NonVisualPictureDrawingProperties()),
                new PIC.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
                new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 990000, Cy = 990000 }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
            var inline = new DW.Inline(new DW.Extent { Cx = 990000, Cy = 990000 }, new DW.DocProperties { Id = 1U, Name = "Picture 1" },
                new A.Graphic(new A.GraphicData(pic) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }));
            body.AppendChild(new Paragraph(new Run(new Drawing(inline))));
        });
        return this;
    }

    public DocxBuilder TrackedInsertion(string text) => Add(new Paragraph(
        new InsertedRun(Run(text)) { Id = "1", Author = "Someone", Date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }));

    public DocxBuilder Header(string line)
    {
        _headerLines.Add(line);
        return this;
    }

    public DocxBuilder Columns(int count)
    {
        _columns = count;
        return this;
    }

    public DocxBuilder WriteProtected()
    {
        _writeProtected = true;
        return this;
    }

    private DocxBuilder Add(Paragraph p)
    {
        _steps.Add((body, _) => body.AppendChild(p));
        return this;
    }

    private static ParagraphProperties Style(string id) => new(new ParagraphStyleId { Val = id });

    private static Run Run(string text, bool bold = false, string? font = null)
    {
        var rpr = new RunProperties();
        if (font is not null) rpr.Append(new RunFonts { Ascii = font, HighAnsi = font });
        if (bold) rpr.Append(new Bold());
        return new Run(rpr, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    public byte[] Build()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body());
            var body = main.Document.Body!;
            AddStyles(main);
            AddNumbering(main);
            foreach (var step in _steps) step(body, main);

            var sect = new SectionProperties(new PageSize { Width = 11906, Height = 16838 });
            if (_columns > 1) sect.Append(new Columns { ColumnCount = (Int16Value)(short)_columns, Space = "720" });
            if (_headerLines.Count > 0)
            {
                var header = main.AddNewPart<HeaderPart>();
                header.Header = new Header(_headerLines.Select(l => new Paragraph(Run(l))));
                sect.InsertAt(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) }, 0);
            }
            body.Append(sect);

            var settings = main.AddNewPart<DocumentSettingsPart>();
            settings.Settings = new Settings();
            if (_writeProtected) settings.Settings.Append(new WriteProtection { Recommended = true });
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = "22" }))),
            new Style(new StyleName { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new Style(new StyleName { Val = "Title" }, new BasedOn { Val = "Normal" },
                new StyleRunProperties(new Bold(), new FontSize { Val = "48" })) { Type = StyleValues.Paragraph, StyleId = "Title" },
            new Style(new StyleName { Val = "heading 1" }, new BasedOn { Val = "Normal" },
                new StyleParagraphProperties(new OutlineLevel { Val = 0 }),
                new StyleRunProperties(new Bold(), new FontSize { Val = "28" })) { Type = StyleValues.Paragraph, StyleId = "Heading1" },
            new Style(new StyleName { Val = "List Paragraph" }, new BasedOn { Val = "Normal" })
                { Type = StyleValues.Paragraph, StyleId = "ListParagraph" });
    }

    private static void AddNumbering(MainDocumentPart main)
    {
        var numbering = main.AddNewPart<NumberingDefinitionsPart>();
        numbering.Numbering = new Numbering(
            new AbstractNum(new Level(new NumberingFormat { Val = NumberFormatValues.Bullet }, new LevelText { Val = "•" })
                { LevelIndex = 0 }) { AbstractNumberId = 1 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
}
