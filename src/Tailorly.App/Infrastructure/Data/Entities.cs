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
