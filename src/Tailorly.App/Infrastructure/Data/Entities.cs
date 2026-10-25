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

// DatabaseDesign §3.7 (needed in M0 for the seed; budgets and usage tables arrive in M1)
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
