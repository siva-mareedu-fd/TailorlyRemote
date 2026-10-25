using System.Globalization;
using Hangfire;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Api;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Time;

namespace Tailorly.App.Features.Accounts;

/// <summary>
/// Authentication endpoints (ApiDesign §4.2). Responses never reveal whether an email has an account: register is
/// always 201, forgot-password always 204, and both checks that can fail (password policy, invite code) run before
/// the account lookup.
/// </summary>
public static partial class AuthEndpoints
{
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/register", Register).WithName("Register").AllowAnonymous().RequireRateLimiting(RateLimiting.AuthRegister);
        auth.MapPost("/verify-email", VerifyEmail).WithName("VerifyEmail").AllowAnonymous();
        auth.MapPost("/resend-verification", ResendVerification).WithName("ResendVerification")
            .RequireAuthorization(AuthPolicies.UserAny).RequireRateLimiting(RateLimiting.AuthMail);
        auth.MapPost("/login", Login).WithName("Login").AllowAnonymous().RequireRateLimiting(RateLimiting.AuthLogin);
        auth.MapPost("/logout", Logout).WithName("Logout").RequireAuthorization(AuthPolicies.UserAny);
        auth.MapPost("/forgot-password", ForgotPassword).WithName("ForgotPassword").AllowAnonymous()
            .RequireRateLimiting(RateLimiting.AuthMail);
        auth.MapPost("/reset-password", ResetPassword).WithName("ResetPassword").AllowAnonymous();
        auth.MapPost("/change-password", ChangePassword).WithName("ChangePassword").RequireAuthorization(AuthPolicies.User);
    }

    internal static string CurrentConsentVersion(IConfiguration config) => config["Consent:Version"] ?? "1";

    internal static ApiException ConsentRequired() => new(StatusCodes.Status403Forbidden, ErrorCodes.ConsentRequired,
        "Consent required", "Please accept the current Terms and Privacy Notice to continue.");

    private static async Task<Created<RegisterResponse>> Register(RegisterRequest body, HttpContext http,
        UserManager<AppUser> users, AppDbContext db, IConfiguration config, AccountRateLimits limits, IBackgroundJobClient jobs,
        ConsentIpHasher ipHash, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new FieldErrors();
        var email = errors.Email("email", body.Email);
        var name = errors.Text("displayName", body.DisplayName, 100, label: "Name");
        var timeZone = string.IsNullOrWhiteSpace(body.TimeZone) ? TimeZones.India : TimeZones.Canonicalize(body.TimeZone);
        if (!TimeZones.IsValid(timeZone)) errors.Add("timeZone", "Unknown time zone.");
        if (string.IsNullOrEmpty(body.Password)) errors.Add("password", "Password is required.");
        else if (body.Password.Length > 256) errors.Add("password", "Password must be 256 characters or fewer.");
        errors.ThrowIfAny();

        if (body.ConsentVersion != CurrentConsentVersion(config)) throw ConsentRequired();
        limits.Acquire(AccountRateLimits.RegisterEmail, email!);

        var candidate = new AppUser { UserName = email, Email = email };
        await ThrowIfWeakPassword(users, candidate, body.Password!, "password");

        string? invite = null;
        if (await FeatureFlags.IsOnAsync(db, FeatureFlag.SignupRequireInviteCode, ct))
        {
            invite = body.InviteCode?.Trim();
            if (string.IsNullOrEmpty(invite))
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, ErrorCodes.InviteCodeRequired,
                    "Invite code required", "Sign-up is invite-only right now. Enter your invite code.");
            if (!await InviteUsableAsync(db, invite, ct)) throw InviteInvalid();
        }

        var existing = await users.FindByEmailAsync(email!);
        if (existing is not null)
            return AlreadyRegistered(jobs, existing.Id);

        var now = clock.GetUtcNow();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (invite is not null && await ConsumeInviteAsync(db, invite, ct) == 0) throw InviteInvalid();
            var created = await users.CreateAsync(candidate, body.Password!);
            if (!created.Succeeded)
            {
                await tx.RollbackAsync(ct);
                if (created.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName")
                    && await users.FindByEmailAsync(email!) is { } raced)
                    return AlreadyRegistered(jobs, raced.Id);
                throw FieldErrors.Single("password", created.Errors.First().Description);
            }
            db.UserProfiles.Add(new UserProfile
            {
                UserId = candidate.Id,
                DisplayName = name!,
                TimeZone = timeZone,
                NotificationEmail = email!,
                ConsentVersion = body.ConsentVersion,
                ConsentedAt = now,
                ConsentIpHash = ipHash.Hash(http),
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        jobs.Enqueue<AccountEmailJob>(j => j.SendAsync(AccountEmailKind.VerifyEmail, candidate.Id, CancellationToken.None));
        LogRegistered(loggers.CreateLogger(typeof(AuthEndpoints)), candidate.Id);
        return TypedResults.Created((string?)null, new RegisterResponse(candidate.Id));
    }

    /// <summary>Same 201 as a new signup; the existing owner gets a "you already have an account" email.</summary>
    private static Created<RegisterResponse> AlreadyRegistered(IBackgroundJobClient jobs, Guid existingId)
    {
        jobs.Enqueue<AccountEmailJob>(j => j.SendAsync(AccountEmailKind.AlreadyRegistered, existingId, CancellationToken.None));
        return TypedResults.Created((string?)null, new RegisterResponse(Guid.CreateVersion7()));
    }

    private static ApiException InviteInvalid() => new(StatusCodes.Status422UnprocessableEntity, ErrorCodes.InviteCodeInvalid,
        "Invite code not valid", "This invite code is not valid, has expired or has been used up.");

    private static async Task<bool> InviteUsableAsync(AppDbContext db, string code, CancellationToken ct) =>
        await db.InviteCodes.AsNoTracking().AnyAsync(i =>
            i.Code == code && i.UsedCount < i.MaxUses && (i.ExpiresAt == null || i.ExpiresAt > DateTimeOffset.UtcNow), ct);

    private static Task<int> ConsumeInviteAsync(AppDbContext db, string code, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE invite_codes SET used_count = used_count + 1
            WHERE code = {code} AND used_count < max_uses AND (expires_at IS NULL OR expires_at > now())
            """, ct);

    internal static async Task ThrowIfWeakPassword(UserManager<AppUser> users, AppUser user, string password, string field)
    {
        var errors = new FieldErrors();
        foreach (var v in users.PasswordValidators)
        {
            var r = await v.ValidateAsync(users, user, password);
            foreach (var e in r.Errors) errors.Add(field, e.Description);
        }
        errors.ThrowIfAny();
    }

    private static async Task<NoContent> VerifyEmail(VerifyEmailRequest body, UserManager<AppUser> users)
    {
        var token = AccountEmailJob.Decode(body.Token);
        var user = token is null ? null : await users.FindByIdAsync(body.UserId.ToString());
        if (user is null || !(await users.ConfirmEmailAsync(user, token!)).Succeeded)
        {
            if (user is { EmailConfirmed: true }) return TypedResults.NoContent(); // link opened twice
            throw FieldErrors.Single("token", "This verification link is invalid or has expired. Ask for a new one.");
        }
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> ResendVerification(HttpContext http, UserManager<AppUser> users, ICurrentUser current,
        AccountRateLimits limits, IBackgroundJobClient jobs)
    {
        limits.Acquire(AccountRateLimits.ResendUser, current.UserId.ToString());
        var user = await users.FindByIdAsync(current.UserId.ToString());
        if (user is { EmailConfirmed: false })
            jobs.Enqueue<AccountEmailJob>(j => j.SendAsync(AccountEmailKind.VerifyEmail, user.Id, CancellationToken.None));
        return TypedResults.NoContent();
    }

    private static async Task<Ok<MeResponse>> Login(LoginRequest body, HttpContext http, UserManager<AppUser> users,
        SignInManager<AppUser> signIn, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var errors = new FieldErrors();
        var email = errors.Text("email", body.Email, 254, label: "Email");
        if (string.IsNullOrEmpty(body.Password)) errors.Add("password", "Password is required.");
        errors.ThrowIfAny();

        var user = await users.FindByEmailAsync(email!);
        if (user is null) throw WrongCredentials();

        var result = await signIn.CheckPasswordSignInAsync(user, body.Password!, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            var end = await users.GetLockoutEndDateAsync(user) ?? clock.GetUtcNow().AddMinutes(15);
            var seconds = Math.Max(1, (int)Math.Ceiling((end - clock.GetUtcNow()).TotalSeconds));
            throw new ApiException(StatusCodes.Status429TooManyRequests, ErrorCodes.AccountLocked, "Too many failed sign-ins",
                    "This account is locked for a few minutes. Try again later or reset your password.")
                .WithHeader("Retry-After", seconds.ToString(CultureInfo.InvariantCulture));
        }
        if (!result.Succeeded) throw WrongCredentials();

        using (CurrentUser.ActAs(user.Id))
        {
            var status = await db.UserProfiles.Where(p => p.UserId == user.Id).Select(p => p.AccountStatus).FirstOrDefaultAsync(ct);
            if (status != AccountStatus.Active)
                throw new ApiException(StatusCodes.Status403Forbidden, ErrorCodes.AccountDisabled, "Account disabled",
                    "This account is being deleted or has been suspended.");
        }

        await signIn.SignInAsync(user, isPersistent: true);
        http.User = await signIn.CreateUserPrincipalAsync(user);
        CsrfProtection.IssueTokens(http); // antiforgery tokens are bound to the signed-in user
        using (CurrentUser.ActAs(user.Id))
            return TypedResults.Ok(await MeEndpoints.BuildMeAsync(db, users, user, ct));
    }

    private static ApiException WrongCredentials() => new(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated,
        "Email or password is incorrect", "Check your email and password and try again.");

    private static async Task<NoContent> Logout(HttpContext http, SignInManager<AppUser> signIn)
    {
        await signIn.SignOutAsync();
        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        CsrfProtection.IssueTokens(http);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> ForgotPassword(ForgotPasswordRequest body, HttpContext http, UserManager<AppUser> users,
        AccountRateLimits limits, IBackgroundJobClient jobs)
    {
        var errors = new FieldErrors();
        var email = errors.Email("email", body.Email);
        errors.ThrowIfAny();
        limits.Acquire(AccountRateLimits.MailEmail, email!);
        if (await users.FindByEmailAsync(email!) is { } user)
            jobs.Enqueue<AccountEmailJob>(j => j.SendAsync(AccountEmailKind.ResetPassword, user.Id, CancellationToken.None));
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> ResetPassword(ResetPasswordRequest body, UserManager<AppUser> users)
    {
        if (string.IsNullOrEmpty(body.NewPassword)) throw FieldErrors.Single("newPassword", "Password is required.");
        var token = AccountEmailJob.Decode(body.Token);
        var user = token is null ? null : await users.FindByIdAsync(body.UserId.ToString());
        if (user is null) throw InvalidResetLink();

        await ThrowIfWeakPassword(users, user, body.NewPassword, "newPassword");
        var result = await users.ResetPasswordAsync(user, token!, body.NewPassword);
        if (!result.Succeeded) throw InvalidResetLink();
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        return TypedResults.NoContent();
    }

    private static ApiException InvalidResetLink() =>
        FieldErrors.Single("token", "This reset link is invalid or has expired. Ask for a new one.");

    private static async Task<NoContent> ChangePassword(ChangePasswordRequest body, HttpContext http, UserManager<AppUser> users,
        SignInManager<AppUser> signIn, ICurrentUser current)
    {
        var errors = new FieldErrors();
        if (string.IsNullOrEmpty(body.CurrentPassword)) errors.Add("currentPassword", "Enter your current password.");
        if (string.IsNullOrEmpty(body.NewPassword)) errors.Add("newPassword", "Enter a new password.");
        errors.ThrowIfAny();

        var user = await users.FindByIdAsync(current.UserId.ToString())
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, "Sign in required");
        await ThrowIfWeakPassword(users, user, body.NewPassword!, "newPassword");
        var result = await users.ChangePasswordAsync(user, body.CurrentPassword!, body.NewPassword!);
        if (!result.Succeeded)
            throw result.Errors.Any(e => e.Code == "PasswordMismatch")
                ? FieldErrors.Single("currentPassword", "Your current password is incorrect.")
                : FieldErrors.Single("newPassword", result.Errors.First().Description);

        await signIn.RefreshSignInAsync(user); // the security stamp changed; keep this session signed in
        http.User = await signIn.CreateUserPrincipalAsync(user);
        CsrfProtection.IssueTokens(http);
        return TypedResults.NoContent();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered user {UserId}")]
    private static partial void LogRegistered(ILogger logger, Guid userId);
}

/// <summary>Reads boolean feature flags from <c>feature_flags</c> (jsonb <c>true</c>/<c>false</c>).</summary>
public static class FeatureFlags
{
    public static async Task<bool> IsOnAsync(AppDbContext db, string key, CancellationToken ct)
    {
        var value = await db.FeatureFlags.AsNoTracking().Where(f => f.Key == key).Select(f => f.Value).FirstOrDefaultAsync(ct);
        return string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }
}
