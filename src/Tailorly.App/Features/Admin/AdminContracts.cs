using System.Text.Json;

namespace Tailorly.App.Features.Admin;

// HTTP contract of ApiDesign §4.11 (the M2 part: sources, companies, coverage, status). Counts and ids only; never job
// or résumé text.

public sealed record SourceHealthDto(
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastError,
    int ConsecutiveFailures,
    DateTimeOffset? CircuitOpenUntil,
    int CallsThisMonth,
    JsonElement? LastRunStats);

/// <param name="HasAdapter">An adapter is built for this source (Jobicy and WWR are not; <c>byo</c> needs none).</param>
/// <param name="OpenJobs">Listings from this source that are open and not suppressed as duplicates.</param>
public sealed record AdminSourceDto(
    string Key,
    string DisplayName,
    string Kind,
    bool Enabled,
    bool HasAdapter,
    bool RequiresAttribution,
    string? AttributionText,
    string? AttributionUrl,
    string? TermsUrl,
    DateTimeOffset? TermsReviewedAt,
    bool RequireSourceApplyUrl,
    bool ForbidThirdPartyResolution,
    bool PurgeOnExpiry,
    bool RequiresLogoAttribution,
    int PollIntervalMinutes,
    int OpenJobs,
    SourceHealthDto Health);

/// <param name="ClearTermsReview">Clears <c>termsReviewedAt</c> (only for a disabled source).</param>
public sealed record UpdateSourceRequest(
    bool? Enabled,
    int? PollIntervalMinutes,
    DateTimeOffset? TermsReviewedAt,
    bool? ClearTermsReview,
    string? AttributionText,
    string? AttributionUrl);

public sealed record RunStartedDto(string RunId);

public sealed record AdminCompanyDto(
    Guid Id,
    Guid CompanyId,
    string Name,
    string? WebsiteDomain,
    string Platform,
    string BoardToken,
    string Status,
    string Origin,
    string Confidence,
    int FailureCount,
    int? OpenJobsCount,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? NextRetryAt,
    int Watchers,
    DateTimeOffset CreatedAt);

public sealed record AdminCompanyPage(IReadOnlyList<AdminCompanyDto> Items, int Page, int PageSize, int Total);

public sealed record AddCompanyRequest(string? Name, string? WebsiteDomain, string? Platform, string? BoardToken);

public sealed record UpdateCompanyRequest(string? Status);

public sealed record SeedLoadResult(int Added, int Skipped);

/// <param name="Worldwide">Eligibility counts. Until M3 enrichment exists they come from the deterministic pre-rules.</param>
public sealed record CoverageSourceDto(string SourceKey, int Total, int Worldwide, int IndiaEligible, int Restricted, int Unclear)
{
    public int OpenToIndia => Worldwide + IndiaEligible;
}

/// <param name="Roles">Role keyword → jobs open to India whose title matches it.</param>
public sealed record CoverageDayDto(DateOnly Date, int Total, int OpenToIndia, IReadOnlyList<CoverageSourceDto> Sources,
    IReadOnlyDictionary<string, int> Roles);

/// <param name="Basis"><c>enrichment_or_rules</c>: the enrichment's final eligibility for enriched jobs, else structured source fields and phrase rules.</param>
/// <param name="Total">Unique listings first seen in the range (suppressed duplicates excluded; by canonical source).</param>
public sealed record CoverageReportDto(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<string> Roles,
    string Basis,
    int Total,
    int OpenToIndia,
    double OpenToIndiaPerDay,
    IReadOnlyList<CoverageSourceDto> Sources,
    IReadOnlyDictionary<string, int> RoleTotals,
    IReadOnlyList<CoverageDayDto> Days);

public sealed record StatusSourceDto(string Key, string DisplayName, bool Enabled, SourceHealthDto Health);

public sealed record StatusJobsDto(int Total, int OpenCanonical, int FirstSeenLast24h, int Limited, int Full);

public sealed record StatusBoardsDto(int Resolved, int NotFound, int Blocked);

public sealed record StatusQueueDto(long Enqueued, long Processing, long Scheduled, long Failed);

public sealed record StatusAiRowDto(string Task, string Model, int Calls, long Tokens, int Errors);

public sealed record StatusDiskDto(long TotalBytes, long FreeBytes, int UsedPercent);

/// <summary>Digest runs created in the last 24 hours (counts only, no user data).</summary>
/// <param name="Unsent">Scheduled digests with items, ready for over an hour and not emailed (SDD §15.2 alert condition).</param>
public sealed record StatusDigestsDto(int Ready, int Failed, int InFlight, int Degraded, int Unsent, DateTimeOffset? LastFinishedAt);

/// <param name="Queue">Hangfire counts; null if the job storage could not be read.</param>
/// <param name="StuckResources">Async resources still queued/running after 30 minutes (swept every 15 minutes).</param>
/// <param name="PlatformEnrichmentCeiling">Null when no ceiling is configured.</param>
public sealed record AdminStatusDto(
    IReadOnlyList<StatusSourceDto> Sources,
    StatusJobsDto Jobs,
    StatusBoardsDto Boards,
    StatusQueueDto? Queue,
    int StuckResources,
    IReadOnlyList<StatusAiRowDto> Ai,
    long PlatformEnrichmentTokensToday,
    long? PlatformEnrichmentCeiling,
    StatusDiskDto? Disk,
    StatusDigestsDto Digests);
