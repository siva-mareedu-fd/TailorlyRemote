using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Tailorly.Docx;

/// <summary>
/// Builds the cover letter .docx (PRD F9, SDD §10.6): the embedded template's styles part (Calibri 11 pt, 10 pt after each
/// paragraph), A4 with 2.5 cm margins, then one plain paragraph per entry. No formatting is taken from the text; it is
/// always plain (characters that are not allowed in XML are dropped).
/// </summary>
public static class CoverLetterWriter
{
    private static readonly Lazy<string> Styles = new(() =>
    {
        using var s = typeof(CoverLetterWriter).Assembly.GetManifestResourceStream("templates/cover-letter-styles.xml")
            ?? throw new InvalidOperationException("The cover letter template is missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    public static byte[] Write(IReadOnlyList<string> paragraphs)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            using (var w = new StreamWriter(styles.GetStream(FileMode.Create))) w.Write(Styles.Value);

            var body = new Body();
            foreach (var text in paragraphs)
                body.AppendChild(new Paragraph(new Run(new Text(XmlSafe(text)) { Space = SpaceProcessingModeValues.Preserve })));
            body.AppendChild(new SectionProperties(
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1418, Right = 1418, Bottom = 1418, Left = 1418, Header = 709, Footer = 709, Gutter = 0 }));
            main.Document = new Document(body);
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static string XmlSafe(string s) =>
        new(s.Where(c => c is '\t' || (c >= 0x20 && c is not ('￾' or '￿'))).Select(c => c == '\t' ? ' ' : c).ToArray());
}
