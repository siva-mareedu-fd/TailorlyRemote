using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Tailorly.Core.Jobs;

/// <summary>
/// Turns untrusted source HTML into plain text (SDD §8.2: "never trust source HTML; store plain text only"). Scripts,
/// styles and comments are dropped with their content, block elements become line breaks, every other tag is removed and
/// entities are decoded. The result is only ever rendered as text, so no sanitiser is needed.
/// </summary>
public static partial class HtmlText
{
    /// <summary>Upper bound for a stored description; anything longer is cut (real descriptions are far shorter).</summary>
    public const int MaxLength = 100_000;

    private const char NoBreakSpace = (char)0xA0;
    private const string Bullet = "•";

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = html.Length > MaxLength * 3 ? html[..(MaxLength * 3)] : html;

        // Greenhouse sends the HTML entity-encoded ("&lt;p&gt;"); decode once so the tags can be removed.
        if (s.IndexOf('<', StringComparison.Ordinal) < 0 && s.Contains("&lt;", StringComparison.Ordinal))
            s = WebUtility.HtmlDecode(s);

        var text = Strip(s);
        // Some listings mix real tags with escaped ones (seen in Remote OK data): decoding revealed tags, so strip again.
        return HtmlTagLike().IsMatch(text) ? Strip(text) : text;
    }

    private static string Strip(string s)
    {
        s = Hidden().Replace(s, " ");
        s = ListItem().Replace(s, "\n" + Bullet + " ");
        s = BlockBreak().Replace(s, "\n");
        s = Tag().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        return Tidy(s);
    }

    /// <summary>Collapses spaces within lines, trims lines and drops empty ones (one line per paragraph or list item).</summary>
    public static string Tidy(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = Spaces().Replace(raw.Replace(NoBreakSpace, ' '), " ").Trim();
            if (line.Length == 0 || line == Bullet) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
            if (sb.Length >= MaxLength) break;
        }
        return sb.Length > MaxLength ? sb.ToString(0, MaxLength) : sb.ToString();
    }

    [GeneratedRegex(@"<(script|style|noscript|template)\b[^>]*>.*?</\1\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<br\s*/?>|</?(p|div|h[1-6]|ul|ol|tr|table|section|article|blockquote|pre|header|footer)\b[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.None, 1000)]
    private static partial Regex Tag();

    [GeneratedRegex(@"</?(p|div|br|span|h[1-6]|ul|ol|li|strong|em|b|i|a|table|tr|td)\b[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HtmlTagLike();

    [GeneratedRegex(@"[ \t\f\v]+", RegexOptions.None, 1000)]
    private static partial Regex Spaces();
}
