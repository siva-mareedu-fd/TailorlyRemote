namespace Tailorly.Core.Ats;

/// <summary>
/// <c>format_health</c> (PRD §8.2, deterministic): starts at 1 and subtracts a fixed penalty per layout problem that ATS
/// parsers commonly trip over. Each problem is also a finding with a fix suggestion. Informational only: the user's
/// layout is never restructured.
/// </summary>
public static class FormatHealth
{
    public const double ColumnsPenalty = 0.15;
    public const double TextBoxesPenalty = 0.15;
    public const double HeaderFooterContactPenalty = 0.15;
    public const double TablesPenalty = 0.10;
    public const double NonStandardHeadingsPenalty = 0.10;
    public const double MissingContactPenalty = 0.10;
    public const double TooLongPenalty = 0.10;
    public const double HeaderFooterTextPenalty = 0.05;
    public const double ImagesPenalty = 0.05;
    public const double UnusualFontsPenalty = 0.05;
    public const double TrackedChangesPenalty = 0.05;

    /// <summary>Pages above which a résumé counts as extremely long.</summary>
    public const int MaxPages = 3;

    /// <summary>Fonts that every common ATS parser and office suite handles (case-insensitive); symbol fonts are ignored.</summary>
    public static readonly IReadOnlySet<string> CommonFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Arial", "Aptos", "Book Antiqua", "Calibri", "Calibri Light", "Cambria", "Candara", "Carlito", "Century Gothic",
        "Constantia", "Consolas", "Corbel", "Courier New", "Garamond", "Georgia", "Helvetica", "Lato", "Liberation Sans",
        "Liberation Serif", "Open Sans", "Palatino Linotype", "Roboto", "Segoe UI", "Tahoma", "Times New Roman",
        "Trebuchet MS", "Verdana", "Arial Narrow", "Noto Sans", "Source Sans Pro", "IBM Plex Sans", "Inter", "Montserrat",
    };

    private static readonly IReadOnlySet<string> SymbolFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Symbol", "Wingdings", "Wingdings 2", "Wingdings 3", "Webdings", "MT Extra", "Marlett",
    };

    public static FormatHealthResult Evaluate(FormatInputs f)
    {
        var findings = new List<FormatFinding>();
        double penalty = 0;
        void Add(bool when, double cost, string code, string severity, string message, string suggestion)
        {
            if (!when) return;
            penalty += cost;
            findings.Add(new FormatFinding(code, severity, message, suggestion));
        }

        Add(f.HasColumns, ColumnsPenalty, "columns", "warn",
            "The résumé uses columns. Some ATS read straight across columns and mix up the text.",
            "Use a single column for your experience and skills.");
        Add(f.HasTextBoxes, TextBoxesPenalty, "text_boxes", "warn",
            "Some text sits in text boxes, which many ATS skip.",
            "Move that text into the main body of the document.");
        Add(f.HeaderFooterContact, HeaderFooterContactPenalty, "header_footer_contact", "warn",
            "Contact details are in the page header or footer, which many ATS ignore.",
            "Put your email and phone number in the first lines of the body.");
        Add(f.HeaderFooterText && !f.HeaderFooterContact, HeaderFooterTextPenalty, "header_footer_text", "info",
            "The page header or footer contains text that some ATS ignore.",
            "Keep important information in the body.");
        Add(f.HasTables, TablesPenalty, "tables", "warn",
            "The résumé uses tables. ATS may read table cells out of order.",
            "Use plain paragraphs for experience and skills.");
        Add(!f.HasStandardHeadings, NonStandardHeadingsPenalty, "nonstandard_headings", "warn",
            "We could not find standard section headings such as Experience and Skills.",
            "Use plain headings: Summary, Experience, Skills, Education.");
        Add(!f.HasContactDetails && !f.HeaderFooterContact, MissingContactPenalty, "missing_contact", "warn",
            "No email address or phone number was found.",
            "Add your email and phone number near your name.");
        Add(f.PageEstimate > MaxPages, TooLongPenalty, "too_long", "info",
            $"The résumé is about {f.PageEstimate} pages long.",
            "Two pages are enough for most roles; trim older or less relevant detail.");
        Add(f.HasImages, ImagesPenalty, "images", "info",
            "The résumé contains images or icons, which ATS ignore.",
            "Make sure nothing important is only in an image.");
        var unusual = f.Fonts.Where(n => !CommonFonts.Contains(n) && !SymbolFonts.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Add(unusual.Count > 0, UnusualFontsPenalty, "unusual_fonts", "info",
            $"Uncommon fonts: {string.Join(", ", unusual)}. They may not render the same everywhere.",
            "Use a common font such as Calibri, Arial or Georgia.");
        Add(f.TrackedChanges, TrackedChangesPenalty, "tracked_changes", "warn",
            "The file contains tracked changes.",
            "Accept or reject all changes in Word, then upload the file again.");

        return new FormatHealthResult(Math.Clamp(1 - penalty, 0, 1), findings);
    }
}
