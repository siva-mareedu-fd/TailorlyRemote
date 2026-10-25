namespace Tailorly.Docx;

/// <summary>Résumé sections (SDD §10.1). Paragraphs before the first heading are the contact block.</summary>
public static class ResumeSections
{
    public const string Contact = "contact";
    public const string Summary = "summary";
    public const string Experience = "experience";
    public const string Skills = "skills";
    public const string Education = "education";
    public const string Projects = "projects";
    public const string Certifications = "certifications";
    public const string Other = "other";
}

/// <summary>Extraction result (SDD §10.1). Ids are <c>p{ordinal}</c> over body paragraphs in document order.</summary>
/// <param name="HeaderFooterLines">Non-empty header/footer lines (read for analysis, never edited).</param>
public sealed record DocModel(
    IReadOnlyList<DocParagraph> Paragraphs,
    IReadOnlyList<string> HeaderFooterLines,
    FormatFindings Findings);

/// <param name="Fingerprint">First 8 hex chars of sha1(normalised text); edits are rejected as stale if it changes.</param>
/// <param name="Text">Paragraph text; tabs and line breaks are mapped to spaces.</param>
public sealed record DocParagraph(
    string Id,
    string Fingerprint,
    string Section,
    string Text,
    IReadOnlyList<DocRun> Runs,
    ParagraphFlags Flags,
    string? Style,
    int? ListLevel);

public sealed record DocRun(int Index, string Text, bool Bold, bool Italic, string? Font, double? SizePt);

/// <param name="LockedPrefixLength">Characters at the start (e.g. a bold "Languages:" label) an edit must keep.</param>
/// <param name="Complex">Contains hyperlinks, fields, tabs/breaks, bookmarks, content controls, images or tracked changes.</param>
/// <param name="LockReason">Why a paragraph is locked (<c>heading</c>, <c>contact</c>, <c>date_line</c>, ...); null if editable.</param>
public sealed record ParagraphFlags(
    bool Editable,
    int LockedPrefixLength,
    bool Complex,
    bool InTable,
    bool InTextBox,
    bool IsHeading,
    bool IsBullet,
    string? LockReason)
{
    public bool Locked => !Editable;
}

/// <summary>
/// Deterministic layout findings stored in <c>resumes.format_findings</c> (DatabaseDesign §4) and used for the ATS
/// format-health score (M4). Contains no résumé text.
/// </summary>
/// <param name="HeaderFooterText">Headers or footers contain text (ATS tools often skip it).</param>
/// <param name="HeaderFooterContact">That text looks like contact details (email or phone).</param>
public sealed record FormatFindings(
    bool HasTables,
    bool HasColumns,
    bool HasTextBoxes,
    bool HasImages,
    bool HeaderFooterText,
    bool HeaderFooterContact,
    IReadOnlyList<string> Fonts,
    int WordCount,
    int PageEstimate,
    bool TrackedChanges,
    int LockedParagraphs,
    int EditableParagraphs,
    int ComplexParagraphs)
{
    public int SchemaVersion { get; init; } = 1;
}
