using Microsoft.AspNetCore.Identity;

namespace Tailorly.App.Infrastructure.Data;

/// <summary>Marker for user-owned entities. Every one gets the EF global query filter <c>UserId == current user</c>.</summary>
public interface IUserOwned
{
    Guid UserId { get; }
}

public sealed class AppUser : IdentityUser<Guid>
{
    public AppUser() => Id = Guid.CreateVersion7();
}

public sealed class AppRole : IdentityRole<Guid>
{
    public const string Admin = "Admin";

    public AppRole() => Id = Guid.CreateVersion7();
    public AppRole(string name) : this() => Name = name;
}

public static class AccountStatus
{
    public const string Active = "active";
    public const string PendingDeletion = "pending_deletion";
    public const string Suspended = "suspended";
}

// DatabaseDesign §3.1
public sealed class UserProfile : IUserOwned
{
    public Guid UserId { get; set; }
    public required string DisplayName { get; set; }
    public string TimeZone { get; set; } = "Asia/Kolkata";
    public required string NotificationEmail { get; set; }
    public string AccountStatus { get; set; } = Data.AccountStatus.Active;
    public string? ConsentVersion { get; set; }
    public DateTimeOffset? ConsentedAt { get; set; }
    public string? ConsentIpHash { get; set; }

    /// <summary>
    /// When a notification email that differs from the sign-in email was confirmed; null = not confirmed, so digests go to
    /// the (verified) sign-in email until it is (proposal 18).
    /// </summary>
    public DateTimeOffset? NotificationEmailVerifiedAt { get; set; }

    /// <summary>Highest résumé version ever issued; versions are never reused after a delete (proposal 15).</summary>
    public int ResumeVersionsIssued { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class InviteCode
{
    public required string Code { get; set; }
    public int MaxUses { get; set; } = 1;
    public int UsedCount { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class FeatureFlag
{
    public const string SignupRequireInviteCode = "signup.require_invite_code";
    public const string DigestEmailEnabled = "digest.email_enabled";
    public const string PdfExportEnabled = "pdf_export.enabled";

    public required string Key { get; set; }

    /// <summary>Raw jsonb value, e.g. <c>true</c>, <c>"text"</c>, <c>{...}</c>.</summary>
    public required string Value { get; set; }
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public string? Entity { get; set; }
    public string? EntityId { get; set; }

    /// <summary>Ids and sizes only; never résumé or job text.</summary>
    public string? Meta { get; set; }
}

/// <summary>Status values of simple async resources (ApiDesign §3).</summary>
public static class WorkStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

// DatabaseDesign §3.2
public sealed class Resume : IUserOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public int Version { get; set; }
    public required string OriginalFilename { get; set; }
    public int SizeBytes { get; set; }

    /// <summary><c>{userId}/resumes/{id}/original.docx</c></summary>
    public required string StorageKey { get; set; }

    public required string Sha256 { get; set; }
    public bool IsActive { get; set; }
    public string ParseStatus { get; set; } = WorkStatus.Queued;
    public string? ErrorCode { get; set; }

    /// <summary>LayoutAnalyzer output (jsonb); no résumé text.</summary>
    public string? FormatFindings { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public static string KeyFor(Guid userId, Guid resumeId) => $"{userId:D}/resumes/{resumeId:D}/original.docx";
    public static string PrefixFor(Guid userId, Guid resumeId) => $"{userId:D}/resumes/{resumeId:D}/";
}

public sealed class ResumeProfile : IUserOwned
{
    public Guid ResumeId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>AI output (jsonb), validated against the resume_parse schema.</summary>
    public required string Parsed { get; set; }

    /// <summary>User corrections (jsonb) laid over <see cref="Parsed"/>; null = none.</summary>
    public string? UserEdited { get; set; }

    public int SchemaVersion { get; set; } = 1;
    public string? PromptVersion { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class UserConfirmedSkill : IUserOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string Skill { get; set; }

    /// <summary>Lower-cased, trimmed; unique per user.</summary>
    public required string SkillKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public static string KeyOf(string skill) => skill.Trim().ToLowerInvariant();
}

// DatabaseDesign §3.7
public sealed class UserBudgetCounter : IUserOwned
{
    public Guid UserId { get; set; }

    /// <summary><c>YYYY-MM</c> in Asia/Kolkata.</summary>
    public required string PeriodKey { get; set; }

    public required string Bucket { get; set; }
    public long LimitTokens { get; set; }
    public long UsedTokens { get; set; }
}

/// <summary>Per-call AI ledger. Written by the system; never holds prompts or replies.</summary>
public sealed class AiUsage
{
    public long Id { get; set; }

    /// <summary>Null for platform calls (enrichment) or after the user was deleted.</summary>
    public Guid? UserId { get; set; }

    public required string Task { get; set; }
    public required string Model { get; set; }
    public string? PromptVersion { get; set; }
    public required string Bucket { get; set; }
    public required string PeriodKey { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheCreateTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CountedTokens { get; set; }
    public decimal? EstCostUsd { get; set; }
    public int? LatencyMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// DatabaseDesign §3.7
public sealed class AiTaskConfig
{
    public required string Task { get; set; }
    public required string Model { get; set; }
    public string? FallbackModel { get; set; }
    public int MaxTokens { get; set; }
    public decimal Temperature { get; set; } = 0.2m;
    public required string Bucket { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

// DatabaseDesign §3.3
public static class SearchScopes
{
    public const string IndiaOnly = "india_only";
    public const string IndiaPlusGlobalRemote = "india_plus_global_remote";
    public const string GlobalRemoteOnly = "global_remote_only";
    public static readonly string[] All = [IndiaOnly, IndiaPlusGlobalRemote, GlobalRemoteOnly];
}

public sealed class UserPreferences : IUserOwned
{
    public Guid UserId { get; set; }
    public string SearchScope { get; set; } = SearchScopes.IndiaPlusGlobalRemote;
    public List<string> TargetRoles { get; set; } = [];
    public List<string> Locations { get; set; } = [];
    public List<string> WorkModes { get; set; } = ["remote"];
    public List<string> JobTypes { get; set; } = ["full_time"];
    public List<string> HiringModels { get; set; } = ["employee", "contractor"];
    public long? SalaryMin { get; set; }
    public long? SalaryMax { get; set; }
    public string SalaryCurrency { get; set; } = "INR";
    public string? ExperienceLevel { get; set; }
    public short MinMatchScore { get; set; } = 8;
    public bool IncludeUnspecifiedSalary { get; set; } = true;
    public bool ShowUnclearEligibility { get; set; }
    public string? TimezoneOverlap { get; set; }
    public List<string> ExcludedCompanies { get; set; } = [];
    public List<string> ExcludedKeywords { get; set; } = [];
    public bool ScheduleEnabled { get; set; } = true;
    public TimeOnly ScheduleLocalTime { get; set; } = new(18, 0);
    public bool DigestWhenEmpty { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>PostgreSQL <c>xmin</c>: the optimistic concurrency token (DatabaseDesign §1).</summary>
    public uint Version { get; set; }
}

/// <summary>A board on the user's watch list (Path B feeder 2). The link itself is shared.</summary>
public sealed class UserWatchedCompany : IUserOwned
{
    public Guid UserId { get; set; }
    public Guid CompanyAtsLinkId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// DatabaseDesign §3.4 (shared tables; no user data)
public static class SourceKinds
{
    public const string Feed = "feed";
    public const string Board = "board";
    public const string User = "user";
}

/// <summary>One job source with its compliance flags (PRD §7.1a). Adapters and the UI read the flags; nothing is hard-coded.</summary>
public sealed class JobSource
{
    public required string Key { get; set; }
    public required string DisplayName { get; set; }
    public required string Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public bool RequiresAttribution { get; set; }
    public string? AttributionText { get; set; }
    public string? AttributionUrl { get; set; }
    public string? TermsUrl { get; set; }
    public DateTimeOffset? TermsReviewedAt { get; set; }
    public bool RequireSourceApplyUrl { get; set; }
    public bool ForbidThirdPartyResolution { get; set; }
    public bool PurgeOnExpiry { get; set; }
    public bool RequiresLogoAttribution { get; set; }
    public int PollIntervalMinutes { get; set; } = 360;
}

public sealed class SourceHealth
{
    public required string SourceKey { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Where an unfinished walk stopped (per stream), so the next run can resume it (PRD §7.1b Path A).</summary>
    public string? LastCursor { get; set; }

    public DateTimeOffset? LastFailureAt { get; set; }

    /// <summary>Short message with a status code; never a payload.</summary>
    public string? LastError { get; set; }

    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? CircuitOpenUntil { get; set; }
    public int CallsThisMonth { get; set; }
    public string? CallsPeriod { get; set; }

    /// <summary>jsonb: fetched/new/updated/duplicate/skipped/failed counts of the last run.</summary>
    public string? LastRunStats { get; set; }
}

public sealed class Company
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public string? WebsiteDomain { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class LinkStatus
{
    public const string Resolved = "resolved";
    public const string NotFound = "not_found";
    public const string Blocked = "blocked";
    public static readonly string[] All = [Resolved, NotFound, Blocked];
}

public static class LinkOrigin
{
    public const string Seed = "seed";
    public const string User = "user";
    public const string Discovered = "discovered";
    public const string Guessed = "guessed";
    public const string Admin = "admin";
}

/// <summary>A company's board on an ATS platform (PRD §7.3). The token is safe by CHECK constraint (SSRF guard).</summary>
public sealed class CompanyAtsLink
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CompanyId { get; set; }
    public required string Platform { get; set; }
    public required string BoardToken { get; set; }
    public string Status { get; set; } = LinkStatus.Resolved;
    public required string Origin { get; set; }
    public string Confidence { get; set; } = "high";
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public int FailureCount { get; set; }
    public int? OpenJobsCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class Completeness
{
    public const string Full = "full";
    public const string Limited = "limited";
}

/// <summary>One row per source listing (DatabaseDesign DB1). Duplicates are marked, never merged.</summary>
public sealed class Job
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string SourceKey { get; set; }
    public required string ExternalId { get; set; }
    public required string DedupeKey { get; set; }
    public Guid? DuplicateOfJobId { get; set; }
    public Guid? CompanyId { get; set; }
    public required string CompanyName { get; set; }
    public required string Title { get; set; }
    public string? LocationText { get; set; }
    public string LocationClass { get; set; } = "unknown";
    public string? WorkMode { get; set; }
    public string? EmploymentType { get; set; }
    public string? HiringModelHint { get; set; }
    public long? SalaryMin { get; set; }
    public long? SalaryMax { get; set; }
    public string? SalaryCurrency { get; set; }
    public string? SalaryPeriodOriginal { get; set; }
    public string SalaryKind { get; set; } = "unknown";

    /// <summary>Plain text only (HTML stripped). Never logged.</summary>
    public string? DescriptionText { get; set; }

    public string DescriptionCompleteness { get; set; } = Completeness.Limited;
    public required string ApplyUrl { get; set; }
    public required string SourceUrl { get; set; }

    /// <summary>jsonb: structured source fields (countries, time zones, tags, departments…).</summary>
    public string? SourceMeta { get; set; }

    public string? ContentHash { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Shared, per-job AI enrichment (DatabaseDesign §3.4, PRD §7.4), created on demand by the first run that needs it and
/// reused by every user. Holds only job-derived data, never user data. <see cref="Eligibility"/> is the final value after
/// the hard rules (SDD §8.6); <see cref="Data"/> is the validated <c>job_enrichment</c> JSON.
/// </summary>
public sealed class JobEnrichment
{
    public Guid JobId { get; set; }

    /// <summary>jsonb: skills, seniority, years, eligibility + evidence + source, time-zone requirement, salary, summary.</summary>
    public required string Data { get; set; }

    public required string Eligibility { get; set; }
    public required string Model { get; set; }
    public string? PromptVersion { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
}

// DatabaseDesign §3.5

/// <summary>One job scored for one user and résumé version (never re-scored). Model <c>rules</c> = rule-based fallback.</summary>
public sealed class UserJobScore : IUserOwned
{
    /// <summary>The <see cref="Model"/> of a rule-ranked row (no AI score; the API shows no fit score for it).</summary>
    public const string RulesModel = "rules";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid JobId { get; set; }
    public int ResumeVersion { get; set; }
    public short FitScore { get; set; }

    /// <summary>jsonb array of strings (≤ 3, ≤ 20 words each).</summary>
    public required string Reasons { get; set; }

    public string MatchedSkills { get; set; } = "[]";
    public string MissingSkills { get; set; } = "[]";
    public required string Confidence { get; set; }
    public required string Model { get; set; }
    public string? PromptVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsRuleBased => Model == RulesModel;
}

public static class DigestKinds
{
    public const string Scheduled = "scheduled";
    public const string Manual = "manual";
}

/// <summary>A run of the per-user pipeline: the daily scheduled digest or a manual "Run now" (DatabaseDesign DB2).</summary>
public sealed class Digest : IUserOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string Kind { get; set; }

    /// <summary>The user's local date the run belongs to (one scheduled digest per user per local date).</summary>
    public DateOnly LocalDate { get; set; }

    public string Status { get; set; } = WorkStatus.Queued;
    public int Attempt { get; set; } = 1;
    public string? ErrorCode { get; set; }
    public int? JobsConsidered { get; set; }
    public int? JobsScored { get; set; }

    /// <summary>True when the rule-based fallback ranked some or all jobs (budget used up, AI unavailable, ceiling hit).</summary>
    public bool Degraded { get; set; }

    /// <summary>Start of the current attempt.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? EmailedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A ranked job in a digest; references the score (DatabaseDesign DB8).</summary>
public sealed class DigestItem : IUserOwned
{
    public Guid DigestId { get; set; }
    public Guid ScoreId { get; set; }
    public Guid UserId { get; set; }
    public short Rank { get; set; }
}
