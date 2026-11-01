namespace Tailorly.App.Features.Tracker;

// HTTP contract of ApiDesign §4.7 (tracker), §4.6 POST /jobs/byo and §4.8 interview prep.

public sealed record UserJobSourceDto(string Key, string Name, string? AttributionUrl);

/// <param name="ResumeStatus">The latest tailored résumé's status (queued … ready | failed), or null.</param>
public sealed record UserJobDocumentsDto(string? AnalysisStatus, string? ResumeStatus, string? CoverLetterStatus, string? InterviewPrepStatus);

public sealed record StatusChangeDto(string Status, DateTimeOffset At);

/// <summary>
/// What the enrichment and the fit score said about a pasted job (BYO or a pasted description), for the "Add a job"
/// result and the workspace header. Null until the enrichment is ready.
/// </summary>
/// <param name="FitReasons">Empty when no fit score was made (no ready résumé, Discovery used up, or the model failed).</param>
public sealed record UserJobAssessmentDto(
    string Eligibility,
    string? EligibilityEvidence,
    string? WorkMode,
    string? Summary,
    IReadOnlyList<string> FitReasons,
    string? FitConfidence,
    IReadOnlyList<string> MatchedSkills,
    IReadOnlyList<string> MissingSkills);

/// <summary>A tracker entry (ApiDesign §4.7 <c>UserJob</c>).</summary>
/// <param name="DescriptionAvailable">False when the listing's text is gone and nothing was pasted.</param>
/// <param name="EnrichmentStatus">
/// For a pasted description: the BYO job's background enrichment (queued → running → ready | failed), or, for a
/// description pasted on a listing, <c>queued</c> until the next AI action enriches it.
/// </param>
/// <param name="EnrichmentErrorCode">Why the background enrichment failed (<c>AI_UNAVAILABLE</c>, <c>BUDGET_EXCEEDED</c>, …).</param>
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
    string? EnrichmentErrorCode,
    UserJobAssessmentDto? Assessment,
    IReadOnlyList<StatusChangeDto> StatusHistory,
    UserJobDocumentsDto Documents,
    string ConcurrencyToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UserJobPage(IReadOnlyList<UserJobDto> Items, int Page, int PageSize, int Total);

public sealed record CreateUserJobRequest(Guid? JobId);

public sealed record UserJobDescriptionRequest(string? Description);

/// <param name="Status">Any status; omitted keeps the current one.</param>
/// <param name="Notes">Omitted (null) keeps the notes; an empty string clears them.</param>
/// <param name="ConcurrencyToken">From the entry as last read; a stale token is 409 <c>CONCURRENT_UPDATE</c>.</param>
public sealed record UpdateUserJobRequest(string? Status, string? Notes, string? ConcurrencyToken);

/// <param name="Description">Required, up to 30,000 characters.</param>
/// <param name="Url">Optional <c>http</c>/<c>https</c> link, stored as text and never fetched.</param>
public sealed record ByoJobRequest(string? Description, string? Url, string? CompanyName, string? Title);

public sealed record PrepTopicDto(string Topic, string Why);

public sealed record PrepQuestionDto(string Question, IReadOnlyList<string> TalkingPointsFromResume);

public sealed record InterviewPrepContentDto(
    IReadOnlyList<PrepTopicDto> TechnicalTopics,
    IReadOnlyList<PrepQuestionDto> Behavioural,
    IReadOnlyList<string> GapsToPrepare,
    IReadOnlyList<string> QuestionsToAsk);

/// <summary>Interview prep for a tracker entry (ApiDesign §4.8 <c>InterviewPrep</c>).</summary>
public sealed record InterviewPrepDto(Guid Id, Guid UserJobId, string Status, string? ErrorCode, InterviewPrepContentDto? Content,
    DateTimeOffset UpdatedAt);
