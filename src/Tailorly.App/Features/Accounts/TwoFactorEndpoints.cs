using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Tailorly.App.Api;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;

namespace Tailorly.App.Features.Accounts;

/// <summary>
/// Two-step sign-in with an authenticator app (TOTP, RFC 6238) through ASP.NET Identity (SDD §14.2: "MFA recommended (TOTP
/// via Identity)"). The shared key and recovery codes live in Identity's own <c>asp_net_user_tokens</c>; nothing new in the
/// schema. Required for admins while <c>Admin:RequireMfa</c> is on (the Admin policy), optional for everyone else.
/// Neither the key, the codes nor the authenticator URI are ever logged or audited.
/// </summary>
public static class TwoFactorEndpoints
{
    private const string Issuer = "Tailorly Remote";
    private const int RecoveryCodeCount = 10;

    public static void MapTwoFactorEndpoints(this RouteGroupBuilder api)
    {
        var tf = api.MapGroup("/me/two-factor").WithTags("Account").RequireAuthorization(AuthPolicies.User);
        tf.MapGet("", GetStatus).WithName("GetTwoFactor");
        tf.MapPost("/setup", Setup).WithName("SetupTwoFactor").RequireRateLimiting(RateLimiting.AuthMail);
        tf.MapPost("/enable", Enable).WithName("EnableTwoFactor").RequireRateLimiting(RateLimiting.AuthLogin);
        tf.MapPost("/disable", Disable).WithName("DisableTwoFactor").RequireRateLimiting(RateLimiting.AuthLogin);
        tf.MapPost("/recovery-codes", NewRecoveryCodes).WithName("NewTwoFactorRecoveryCodes").RequireRateLimiting(RateLimiting.AuthLogin);
    }

    private static async Task<AppUser> RequireUserAsync(UserManager<AppUser> users, ICurrentUser current) =>
        await users.FindByIdAsync(current.UserId.ToString())
        ?? throw new ApiException(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, "Sign in required");

    private static async Task<Ok<TwoFactorStatus>> GetStatus(UserManager<AppUser> users, ICurrentUser current)
    {
        var user = await RequireUserAsync(users, current);
        return TypedResults.Ok(await StatusAsync(users, user));
    }

    private static async Task<TwoFactorStatus> StatusAsync(UserManager<AppUser> users, AppUser user) =>
        new(await users.GetTwoFactorEnabledAsync(user), await users.CountRecoveryCodesAsync(user));

    /// <summary>Starts setup: a new shared key (any earlier unconfirmed key is replaced). Refused while it is on.</summary>
    private static async Task<Ok<TwoFactorSetup>> Setup(UserManager<AppUser> users, ICurrentUser current)
    {
        var user = await RequireUserAsync(users, current);
        if (await users.GetTwoFactorEnabledAsync(user))
            throw FieldErrors.Single("code", "Two-step sign-in is already on. Turn it off first to use a new authenticator.");
        await users.ResetAuthenticatorKeyAsync(user);
        var key = await users.GetAuthenticatorKeyAsync(user)
            ?? throw new InvalidOperationException("Identity did not store an authenticator key.");
        var label = UrlEncoder.Default.Encode($"{Issuer}:{user.Email}");
        var issuer = UrlEncoder.Default.Encode(Issuer);
        return TypedResults.Ok(new TwoFactorSetup(Group(key), $"otpauth://totp/{label}?secret={key}&issuer={issuer}&digits=6"));
    }

    /// <summary>Confirms setup with a code from the app, turns two-step sign-in on and returns one-time recovery codes.</summary>
    private static async Task<Ok<TwoFactorRecoveryCodes>> Enable(TwoFactorCodeRequest body, HttpContext http,
        UserManager<AppUser> users, SignInManager<AppUser> signIn, ICurrentUser current, AuditWriter audit, CancellationToken ct)
    {
        var user = await RequireUserAsync(users, current);
        if (await users.GetTwoFactorEnabledAsync(user)) throw FieldErrors.Single("code", "Two-step sign-in is already on.");
        if (await users.GetAuthenticatorKeyAsync(user) is null) throw FieldErrors.Single("code", "Start the setup first.");
        if (!await VerifyTotpAsync(users, user, body.Code)) throw WrongCode();

        await users.SetTwoFactorEnabledAsync(user, true); // rotates the security stamp: other sessions must sign in again
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount))?.ToList() ?? [];
        await RefreshAsync(http, signIn, user);
        await audit.WriteAsync("account.two_factor.enabled", user.Id, "user", user.Id.ToString("D"), null, ct);
        return TypedResults.Ok(new TwoFactorRecoveryCodes(codes));
    }

    /// <summary>Turns two-step sign-in off; needs the current password. Removes the key and the recovery codes.</summary>
    private static async Task<Ok<TwoFactorStatus>> Disable(TwoFactorPasswordRequest body, HttpContext http,
        UserManager<AppUser> users, SignInManager<AppUser> signIn, ICurrentUser current, AuditWriter audit, CancellationToken ct)
    {
        var user = await RequireUserAsync(users, current);
        await RequirePasswordAsync(users, user, body.Password);
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user); // a later setup starts with a new key
        await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
        await RefreshAsync(http, signIn, user);
        await audit.WriteAsync("account.two_factor.disabled", user.Id, "user", user.Id.ToString("D"), null, ct);
        return TypedResults.Ok(await StatusAsync(users, user));
    }

    /// <summary>Replaces the recovery codes (the old ones stop working); needs the current password.</summary>
    private static async Task<Ok<TwoFactorRecoveryCodes>> NewRecoveryCodes(TwoFactorPasswordRequest body,
        UserManager<AppUser> users, ICurrentUser current, AuditWriter audit, CancellationToken ct)
    {
        var user = await RequireUserAsync(users, current);
        await RequirePasswordAsync(users, user, body.Password);
        if (!await users.GetTwoFactorEnabledAsync(user)) throw FieldErrors.Single("password", "Two-step sign-in is off.");
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount))?.ToList() ?? [];
        await audit.WriteAsync("account.two_factor.recovery_codes", user.Id, "user", user.Id.ToString("D"), null, ct);
        return TypedResults.Ok(new TwoFactorRecoveryCodes(codes));
    }

    /// <summary>
    /// The sign-in step (called by <c>POST /auth/login</c> after the password was accepted): a 6-digit authenticator code,
    /// or one of the recovery codes, which is then used up. A wrong code counts as a failed sign-in for lockout.
    /// </summary>
    internal static async Task<bool> VerifySignInCodeAsync(UserManager<AppUser> users, AppUser user, string code)
    {
        var ok = await VerifyTotpAsync(users, user, code)
                 || (await users.RedeemTwoFactorRecoveryCodeAsync(user, code.Trim())).Succeeded;
        if (ok) await users.ResetAccessFailedCountAsync(user);
        else await users.AccessFailedAsync(user);
        return ok;
    }

    private static async Task<bool> VerifyTotpAsync(UserManager<AppUser> users, AppUser user, string? code)
    {
        var digits = new string((code ?? "").Where(c => c is not (' ' or '-')).ToArray());
        if (digits.Length != 6 || !digits.All(char.IsAsciiDigit)) return false;
        return await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, digits);
    }

    private static async Task RequirePasswordAsync(UserManager<AppUser> users, AppUser user, string? password)
    {
        if (string.IsNullOrEmpty(password) || !await users.CheckPasswordAsync(user, password))
            throw FieldErrors.Single("password", "Your password is incorrect.");
    }

    private static async Task RefreshAsync(HttpContext http, SignInManager<AppUser> signIn, AppUser user)
    {
        await signIn.RefreshSignInAsync(user); // keep this session signed in after the stamp changed
        http.User = await signIn.CreateUserPrincipalAsync(user);
        CsrfProtection.IssueTokens(http);
    }

    private static ApiException WrongCode() =>
        FieldErrors.Single("code", "That code is not right. Check the time on your phone and use the newest code.");

    /// <summary>The Base32 key in groups of four, easier to type into an app by hand.</summary>
    private static string Group(string key) =>
        string.Join(' ', Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))))
            .ToLowerInvariant();
}

public sealed record TwoFactorStatus(bool Enabled, int RecoveryCodesLeft);

/// <param name="SharedKey">Base32 key for manual entry (grouped by four).</param>
/// <param name="AuthenticatorUri"><c>otpauth://</c> URI for the QR code.</param>
public sealed record TwoFactorSetup(string SharedKey, string AuthenticatorUri);

public sealed record TwoFactorRecoveryCodes(IReadOnlyList<string> RecoveryCodes);

public sealed record TwoFactorCodeRequest(string? Code);

public sealed record TwoFactorPasswordRequest(string? Password);
