namespace Tailorly.App.Features.Accounts;

// HTTP contract of ApiDesign §4.2 and §4.3. Request DTOs bind only the listed fields (no mass assignment).

public sealed record RegisterRequest(
    string? Email, string? Password, string? DisplayName, string? TimeZone, string? ConsentVersion, string? InviteCode);

public sealed record RegisterResponse(Guid UserId);

public sealed record VerifyEmailRequest(Guid UserId, string? Token);

public sealed record LoginRequest(string? Email, string? Password);

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(Guid UserId, string? Token, string? NewPassword);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record ConfirmDeletionRequest(Guid UserId, string? Token);

public sealed record DeletionStatusResponse(string Status);

public sealed record ConsentInfo(string Version, DateTimeOffset At);

public sealed record MeResponse(
    Guid Id,
    string Email,
    bool EmailVerified,
    string DisplayName,
    string TimeZone,
    string NotificationEmail,
    ConsentInfo? Consent,
    string AccountStatus,
    IReadOnlyList<string> Roles);

public sealed record UpdateMeRequest(string? DisplayName, string? TimeZone, string? NotificationEmail);

public sealed record ConsentRequest(string? Version);
