namespace Tailorly.Core.Ats;

/// <summary>
/// Weights of the ATS Match Score (PRD §8.2), bound from configuration (<c>Ats:Weights</c>). Defaults are the PRD's.
/// They need not sum to 1: the calculator divides by the sum of the weights of the components it has.
/// </summary>
public sealed class AtsWeights
{
    public double KeywordCoverage { get; set; } = 0.35;
    public double RequirementsMatch { get; set; } = 0.25;
    public double TitleSeniority { get; set; } = 0.10;
    public double ExperienceRelevance { get; set; } = 0.15;
    public double FormatHealth { get; set; } = 0.15;
}

/// <summary>Requirement and skill types (PRD Appendix B).</summary>
public static class RequirementTypes
{
    public const string Required = "required";
    public const string Preferred = "preferred";
}

/// <summary>Per-requirement status from <c>ats_analysis</c> (PRD §8.2).</summary>
public static class RequirementStatuses
{
    public const string Met = "met";
    public const string Partial = "partial";
    public const string Missing = "missing";
}

/// <summary>The five components, each 0–1. Null = no data for it (e.g. the job lists no skills); it is left out of the score.</summary>
public sealed record AtsComponents(
    double? KeywordCoverage,
    double? RequirementsMatch,
    double? TitleSeniority,
    double? ExperienceRelevance,
    double? FormatHealth);

/// <summary>A job skill and the other spellings that count as the same skill (enrichment <c>skill_aliases</c>).</summary>
public sealed record SkillTerm(string Skill, IReadOnlyList<string> Aliases);

/// <summary>What the job asks for, from its enrichment (PRD §7.4).</summary>
public sealed record JobSkillSet(IReadOnlyList<SkillTerm> Required, IReadOnlyList<SkillTerm> Preferred);

/// <param name="Score">Weighted share of the job's skills found in the résumé (required 1.0, preferred 0.5); null if the job lists none.</param>
/// <param name="Matched">Skills found, in the job's spelling.</param>
/// <param name="Missing">Skills not found, in the job's spelling.</param>
/// <param name="MissingRequired">The missing skills that are required (candidates for the skill-confirmation step).</param>
public sealed record KeywordCoverageResult(
    double? Score,
    IReadOnlyList<string> Matched,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> MissingRequired);

/// <summary>A requirement as the score sees it: its type and the status the model gave it.</summary>
public sealed record RequirementStatus(string Type, string Status);

/// <param name="JobSeniority">intern | junior | mid | senior | staff | principal | lead | manager (enrichment), or null.</param>
/// <param name="RecentTitles">The candidate's most recent job titles, newest first.</param>
/// <param name="CandidateYears">Total years of experience from the parsed profile.</param>
/// <param name="ModelLabel">The model's 0–1 title/seniority alignment (<c>title_seniority_label</c>), or null.</param>
public sealed record SeniorityInput(
    string? JobSeniority,
    int? JobYearsMin,
    int? JobYearsMax,
    IReadOnlyList<string> RecentTitles,
    double? CandidateYears,
    double? ModelLabel);

/// <summary>
/// Layout facts for format health (PRD §8.2), from the upload-time layout analysis plus two facts from the extracted
/// text. No résumé text.
/// </summary>
/// <param name="HasStandardHeadings">An Experience and a Skills heading (or equivalents) were recognised.</param>
/// <param name="HasContactDetails">An email address or phone number appears in the body.</param>
public sealed record FormatInputs(
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
    bool HasStandardHeadings,
    bool HasContactDetails);

/// <summary>One format finding with a fix suggestion (informational; the layout is never restructured).</summary>
/// <param name="Code">Stable code, e.g. <c>columns</c>, <c>header_footer_contact</c>.</param>
/// <param name="Severity">info | warn.</param>
public sealed record FormatFinding(string Code, string Severity, string Message, string? Suggestion);

public sealed record FormatHealthResult(double Score, IReadOnlyList<FormatFinding> Findings);

/// <param name="Score">0–100, rounded half away from zero.</param>
public sealed record AtsScore(int Score, AtsComponents Components);
