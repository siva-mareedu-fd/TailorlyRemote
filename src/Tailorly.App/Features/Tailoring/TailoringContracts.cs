using System.Text.Json;
using Tailorly.Core.Ats;
using Tailorly.Docx.Tailoring;

namespace Tailorly.App.Features.Tailoring;

// HTTP contract of ApiDesign §4.7 (the slice M4 needs) and §4.8.

public sealed record UserJobSourceDto(string Key, string Name, string? AttributionUrl);

/// <param name="ResumeStatus">The latest tailored résumé's status (queued … ready | failed), or null.</param>
public sealed record UserJobDocumentsDto(string? AnalysisStatus, string? ResumeStatus, string? CoverLetterStatus, string? InterviewPrepStatus);

public sealed record StatusChangeDto(string Status, DateTimeOffset At);

/// <summary>A tracker entry (ApiDesign §4.7 <c>UserJob</c>).</summary>
/// <param name="DescriptionAvailable">False when the listing's text is gone and nothing was pasted.</param>
/// <param name="EnrichmentStatus">For a pasted description: <c>ready</c> once enriched, <c>queued</c> until the next AI action.</param>
public sealed record UserJobDto(
    Guid Id,
    Guid? JobId,
    bool IsByo,
    string Title,
    string CompanyName,
    string? ApplyUrl,
    UserJobSourceDto? Source,
    string Status,
    string? Notes,
    int? FitScore,
    int? AtsBefore,
    int? AtsAfter,
    bool HasUserDescription,
    bool DescriptionAvailable,
    string? EnrichmentStatus,
    IReadOnlyList<StatusChangeDto> StatusHistory,
    UserJobDocumentsDto Documents,
    string ConcurrencyToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateUserJobRequest(Guid? JobId);

public sealed record UserJobDescriptionRequest(string? Description);

/// <summary>The five ATS components, each 0–1; null when the component had no data (it is left out of the score).</summary>
public sealed record AtsComponentsDto(double? KeywordCoverage, double? RequirementsMatch, double? TitleSeniority,
    double? ExperienceRelevance, double? FormatHealth)
{
    public static AtsComponentsDto From(AtsComponents c) =>
        new(c.KeywordCoverage, c.RequirementsMatch, c.TitleSeniority, c.ExperienceRelevance, c.FormatHealth);
}

/// <param name="Type">required | preferred.</param>
/// <param name="Status">met | partial | missing.</param>
public sealed record RequirementDto(string Text, string Type, string Status, IReadOnlyList<string> EvidenceParagraphIds);

/// <param name="Confirmed">The skill is in the user's confirmed skills.</param>
public sealed record GapDto(string Skill, bool LikelyHas, bool Confirmed);

/// <param name="Severity">info | warn.</param>
public sealed record FindingDto(string Code, string Severity, string Message, string? Suggestion);

/// <summary>The job's skills found and not found in the résumé (keyword coverage).</summary>
public sealed record KeywordsDto(IReadOnlyList<string> Matched, IReadOnlyList<string> Missing);

/// <summary>ATS analysis of the original résumé (ApiDesign §4.8 <c>Analysis</c>). Poll <c>GET /user-jobs/{id}/analysis</c>.</summary>
public sealed record AnalysisDto(
    Guid Id,
    Guid UserJobId,
    Guid ResumeId,
    string Status,
    string? ErrorCode,
    int? AtsScore,
    AtsComponentsDto? Components,
    IReadOnlyList<RequirementDto> Requirements,
    IReadOnlyList<GapDto> Gaps,
    IReadOnlyList<FindingDto> Findings,
    KeywordsDto? Keywords,
    string Disclaimer,
    DateTimeOffset UpdatedAt);

public sealed record AnalysisRequest(Guid? ResumeId);

/// <param name="Type">equal | insert | delete.</param>
public sealed record DiffSegmentDto(string Type, string Text);

/// <param name="Flags">new_fact | length | formatting_simplified.</param>
/// <param name="NewTerms">What a <c>new_fact</c> edit adds that the user never stated; accepting needs each one confirmed.</param>
/// <param name="Decision">pending | accepted | rejected.</param>
public sealed record TailoredEditDto(
    string Id,
    string ParagraphId,
    string Section,
    string Original,
    string Proposed,
    string Rationale,
    IReadOnlyList<DiffSegmentDto> Diff,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> NewTerms,
    string Decision);

public sealed record CoverLetterContentDto(IReadOnlyList<string> Paragraphs, int WordCount, string? Notes);

/// <summary>A tailored résumé or a cover letter (ApiDesign §4.8 <c>TailoredDocument</c>). Poll <c>GET /tailored-documents/{id}</c>.</summary>
/// <param name="Status">queued | running | proposed | finalizing | ready | failed.</param>
/// <param name="AtsAfter">While proposed: projected from the accepted edits; once ready: re-scored on the final text.</param>
/// <param name="AtsAfterBasis">projected | rescored (re-scoring falls back to the projection if the AI is unavailable).</param>
public sealed record TailoredDocumentDto(
    Guid Id,
    Guid UserJobId,
    string Kind,
    string Status,
    string? ErrorCode,
    int SourceResumeVersion,
    int? AtsBefore,
    int? AtsAfter,
    AtsComponentsDto? AtsBreakdown,
    string? AtsAfterBasis,
    IReadOnlyList<TailoredEditDto> Edits,
    IReadOnlyList<string> UnaddressedGaps,
    string? Tone,
    CoverLetterContentDto? Content,
    bool Downloadable,
    IReadOnlyList<string> Formats,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? FinalizedAt);

public sealed record TailorRequest(Guid? ResumeId);

/// <param name="Decision">accepted | rejected | pending (pending resets a decision).</param>
public sealed record DecisionItem(string? EditId, string? Decision);

public sealed record DecisionsRequest(IReadOnlyList<DecisionItem>? Decisions);

/// <param name="Tone">concise (default) | confident.</param>
/// <param name="Notes">Optional, ≤ 500 characters (for example a referral).</param>
public sealed record CoverLetterRequest(string? Tone, string? Notes, Guid? ResumeId);

public sealed record CoverLetterContentRequest(IReadOnlyList<string>? Paragraphs);

public static class EditDecisions
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

public static class AtsBasis
{
    public const string Projected = "projected";
    public const string Rescored = "rescored";
}

// ---------- stored JSON (jsonb columns; camelCase, versioned) ----------

/// <summary><c>tailored_documents.edits</c> for a résumé.</summary>
public sealed record StoredEdits(int SchemaVersion, IReadOnlyList<StoredEdit> Items, IReadOnlyList<string> UnaddressedGaps)
{
    public const int CurrentVersion = 1;
}

/// <param name="NewText">The whole new paragraph, starting with the locked prefix exactly as in the résumé.</param>
public sealed record StoredEdit(
    string Id,
    string ParagraphId,
    string Fingerprint,
    string Section,
    string Original,
    string NewText,
    string Rationale,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> NewTerms,
    string Decision)
{
    public static StoredEdit From(CheckedEdit e) => new(e.Id, e.ParagraphId, e.Fingerprint, e.Section, e.Original, e.Proposed,
        e.Rationale, e.Flags, e.NewTerms, e.Flags.Contains(EditFlags.NewFact) ? EditDecisions.Rejected : EditDecisions.Pending);
}

/// <summary><c>tailored_documents.content</c> for a cover letter; notes are kept here, never in the job queue.</summary>
public sealed record StoredLetter(IReadOnlyList<string> Paragraphs, int WordCount, string? Notes);

/// <summary>
/// <c>ats_analyses.components</c> and <c>tailored_documents.ats_breakdown</c>; for a re-scored résumé also the requirement
/// statuses of the final text.
/// </summary>
public sealed record StoredScore(AtsComponentsDto Components, KeywordsDto Keywords, string? Basis = null,
    IReadOnlyList<StoredRequirement>? Requirements = null);

public sealed record StoredGap(string Skill, bool LikelyHas);

internal static class TailoringJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
