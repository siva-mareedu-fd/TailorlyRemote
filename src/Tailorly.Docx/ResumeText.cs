using System.Text;

namespace Tailorly.Docx;

/// <summary>Plain-text view of an extracted résumé for AI tasks (header/footer lines first, then paragraphs in order).</summary>
public static class ResumeText
{
    public static string Build(DocModel model)
    {
        var sb = new StringBuilder();
        foreach (var line in model.HeaderFooterLines)
            sb.Append("[header/footer] ").AppendLine(line);
        foreach (var p in model.Paragraphs)
        {
            var t = ResumeExtractor.Normalize(p.Text);
            if (t.Length == 0) continue;
            if (p.Flags.IsHeading) sb.AppendLine().Append("## ");
            sb.AppendLine(t);
        }
        return sb.ToString().Trim();
    }
}
