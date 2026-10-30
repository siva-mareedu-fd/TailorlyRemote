namespace Tailorly.Core.Matching;

/// <summary>Search scope values of <c>user_preferences.search_scope</c> (PRD F3).</summary>
public static class Scope
{
    public const string IndiaOnly = "india_only";
    public const string IndiaPlusGlobalRemote = "india_plus_global_remote";
    public const string GlobalRemoteOnly = "global_remote_only";
}

/// <summary>The parts of a user's preferences that matching reads (PRD F3, §7.5).</summary>
public sealed record MatchPreferences
{
    public string SearchScope { get; init; } = Scope.IndiaPlusGlobalRemote;
    public IReadOnlyList<string> TargetRoles { get; init; } = [];
    public IReadOnlyList<string> Locations { get; init; } = [];
    public IReadOnlyList<string> WorkModes { get; init; } = ["remote"];
    public IReadOnlyList<string> JobTypes { get; init; } = ["full_time"];
    public IReadOnlyList<string> HiringModels { get; init; } = ["employee", "contractor"];
    public IReadOnlyList<string> ExcludedCompanies { get; init; } = [];
    public IReadOnlyList<string> ExcludedKeywords { get; init; } = [];
    public long? SalaryMin { get; init; }
    public long? SalaryMax { get; init; }
    public string SalaryCurrency { get; init; } = "INR";
    public int MinMatchScore { get; init; } = 8;
    public bool IncludeUnspecifiedSalary { get; init; } = true;
    public bool ShowUnclearEligibility { get; init; }
}

/// <summary>A listing as the raw-field prefilter sees it (SDD §8.5 step 1): no enrichment yet.</summary>
/// <param name="Countries">Structured country restrictions (Himalayas), or null when the source has none.</param>
/// <param name="DescriptionHead">The first 500 characters of the plain-text description.</param>
public sealed record RawCandidate(
    Guid JobId,
    string Title,
    string CompanyName,
    string? LocationText,
    string? WorkMode,
    string? EmploymentType,
    IReadOnlyList<string>? Countries,
    string? DescriptionHead);

/// <param name="Relevance">Higher is more relevant; orders enrichment and the scoring shortlist.</param>
/// <param name="Reason">Short, content-free reason code (logged in counts only).</param>
public sealed record PrefilterVerdict(bool Keep, double Relevance, string Reason)
{
    public static PrefilterVerdict Drop(string reason) => new(false, 0, reason);
}

/// <summary>What the enriched prefilter reads: the final eligibility plus enrichment (or source) fields.</summary>
/// <param name="IndiaLocated">The job is located in India (an Indian city or India in its structured country list).</param>
public sealed record EnrichedFacts(
    string Eligibility,
    string? WorkMode,
    string? EmploymentType,
    string? HiringModel,
    string? LocationText,
    bool IndiaLocated);
