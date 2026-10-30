namespace Tailorly.App.Features.Jobs;

// HTTP contract of ApiDesign §2 (shared job types) and §4.6.

/// <summary>Source attribution, present on every job (PRD F14). Render <see cref="AttributionText"/> as a followed link.</summary>
public sealed record SourceInfoDto(string Key, string Name, string? AttributionText, string? AttributionUrl, string SourceUrl);

public sealed record ApproxInrDto(long? Min, long? Max);

/// <param name="Period">Always <c>year</c> (amounts are annualised).</param>
/// <param name="OriginalPeriod">hour | day | week | fortnight | month | year, as published.</param>
/// <param name="Kind">stated | estimated | unknown.</param>
/// <param name="ApproxInr">Present only when the currency is not INR and a rate is known; shown as "≈".</param>
public sealed record SalaryDto(long? Min, long? Max, string? Currency, string Period, string? OriginalPeriod, string Kind,
    ApproxInrDto? ApproxInr);

public sealed record AlsoOnDto(string SourceKey, string SourceName, string SourceUrl);

/// <param name="Confidence">high | medium | low.</param>
public sealed record JobScoreDto(int FitScore, IReadOnlyList<string> Reasons, IReadOnlyList<string> MatchedSkills,
    IReadOnlyList<string> MissingSkills, string Confidence);

public sealed record TrackedDto(Guid UserJobId, string Status);

/// <summary>A job scored for the caller (ApiDesign §2 <c>JobSummary</c>).</summary>
public record JobSummaryDto
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public required string CompanyName { get; init; }
    public string? LocationText { get; init; }

    /// <summary>remote | hybrid | onsite, or null.</summary>
    public string? WorkMode { get; init; }

    public string? EmploymentType { get; init; }

    /// <summary>worldwide | india_eligible | restricted | unclear.</summary>
    public required string Eligibility { get; init; }

    public string? EligibilityEvidence { get; init; }
    public required SalaryDto Salary { get; init; }

    /// <summary>stated | estimated | not_listed.</summary>
    public required string SalaryLabel { get; init; }

    /// <summary>full | limited.</summary>
    public required string DescriptionCompleteness { get; init; }

    public DateTimeOffset? PostedAt { get; init; }

    /// <summary>The URL the source supplied when its <c>require_source_apply_url</c> flag is set; never an ATS link we substituted.</summary>
    public required string ApplyUrl { get; init; }

    public required SourceInfoDto Source { get; init; }
    public required IReadOnlyList<AlsoOnDto> AlsoOn { get; init; }

    /// <summary>Null when the job was ranked by the rule-based fallback (no AI score).</summary>
    public JobScoreDto? Score { get; init; }

    /// <summary>The caller's tracker entry for this job (tracker arrives in M5; null until then).</summary>
    public TrackedDto? Tracked { get; init; }
}

/// <summary>ApiDesign §2 <c>JobDetail</c>.</summary>
public sealed record JobDetailDto : JobSummaryDto
{
    /// <summary>Plain text; null if purged or not supplied by the source.</summary>
    public string? Description { get; init; }

    /// <summary>Enrichment summary (≤ 60 words).</summary>
    public string? Summary { get; init; }

    public required IReadOnlyList<string> SkillsRequired { get; init; }
    public required IReadOnlyList<string> SkillsPreferred { get; init; }
    public string? Seniority { get; init; }
    public string? TimezoneRequirement { get; init; }
}

public sealed record JobPage(IReadOnlyList<JobSummaryDto> Items, int Page, int PageSize, int Total);
