using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Infrastructure;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Accounts;

public enum AccountEmailKind
{
    VerifyEmail,
    AlreadyRegistered,
    ResetPassword,
    ConfirmDeletion,
}

/// <summary>
/// <c>SendEmail</c> for account mail (SDD §11.5): a Hangfire job retried 3 times (1/5/30 min). Job arguments are only
/// the kind and the user id; links and tokens are minted when the job runs, so no token is stored in the job queue.
/// Resending is safe (a fresh token). Recipients and bodies are never logged.
/// </summary>
public sealed partial class AccountEmailJob(
    UserManager<AppUser> users,
    AppDbContext db,
    IEmailSender sender,
    AppLinks links,
    AccountDeletionTokens deletionTokens,
    ILogger<AccountEmailJob> logger)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 300, 1800])]
    public async Task SendAsync(AccountEmailKind kind, Guid userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user?.Email is null)
        {
            LogSkipped(logger, kind, userId);
            return;
        }

        string? name;
        using (CurrentUser.ActAs(userId))
            name = await db.UserProfiles.Where(p => p.UserId == userId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct);

        var message = kind switch
        {
            AccountEmailKind.VerifyEmail => EmailTemplates.VerifyEmail(user.Email, name, links.Spa("/verify-email", new Dictionary<string, string?>
            {
                ["userId"] = userId.ToString(),
                ["token"] = Encode(await users.GenerateEmailConfirmationTokenAsync(user)),
            })),
            AccountEmailKind.AlreadyRegistered => EmailTemplates.AlreadyRegistered(user.Email, name,
                links.Spa("/signin", new Dictionary<string, string?>())),
            AccountEmailKind.ResetPassword => EmailTemplates.ResetPassword(user.Email, name, links.Spa("/reset-password", new Dictionary<string, string?>
            {
                ["userId"] = userId.ToString(),
                ["token"] = Encode(await users.GeneratePasswordResetTokenAsync(user)),
            })),
            AccountEmailKind.ConfirmDeletion => EmailTemplates.ConfirmDeletion(user.Email, name, links.Spa("/confirm-deletion", new Dictionary<string, string?>
            {
                ["userId"] = userId.ToString(),
                ["token"] = deletionTokens.Create(userId, await users.GetSecurityStampAsync(user)),
            })),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        await sender.SendAsync(message, ct);
        LogSent(logger, kind, userId);
    }

    /// <summary>Identity tokens are base64; links carry them base64url-encoded.</summary>
    public static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    public static string? Decode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2000) return null;
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Kind} email for user {UserId}")]
    private static partial void LogSent(ILogger logger, AccountEmailKind kind, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped {Kind} email: user {UserId} no longer exists")]
    private static partial void LogSkipped(ILogger logger, AccountEmailKind kind, Guid userId);
}

/// <summary>
/// Signed, purpose-bound, 1-hour account-deletion links (SDD §7.5). The payload binds the user id and the security
/// stamp, so a password change or sign-out-everywhere invalidates an outstanding link.
/// </summary>
public sealed class AccountDeletionTokens(IDataProtectionProvider provider, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private readonly ITimeLimitedDataProtector _protector =
        provider.CreateProtector("Tailorly.AccountDeletion.v1").ToTimeLimitedDataProtector();

    public string Create(Guid userId, string securityStamp) =>
        _protector.Protect($"{userId:D}|{securityStamp}", clock.GetUtcNow().Add(Lifetime));

    /// <summary>True when the token is authentic, unexpired and matches the user's current security stamp.</summary>
    public bool Validate(string? token, Guid userId, string? securityStamp)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2000 || securityStamp is null) return false;
        try
        {
            var payload = _protector.Unprotect(token, out var expires);
            return expires > clock.GetUtcNow() && payload == $"{userId:D}|{securityStamp}";
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>Plain account-mail templates. User-supplied values are HTML-encoded; no user-controlled headers.</summary>
public static class EmailTemplates
{
    private static string Greeting(string? name) => string.IsNullOrWhiteSpace(name) ? "Hello," : $"Hello {name},";

    public static EmailMessage VerifyEmail(string to, string? name, string link) => Build(to,
        "Verify your email for Tailorly Remote", name,
        "Please confirm your email address to start using Tailorly Remote. The link works for 24 hours.",
        "Verify my email", link,
        "If you did not create an account, you can ignore this email.");

    public static EmailMessage AlreadyRegistered(string to, string? name, string link) => Build(to,
        "You already have a Tailorly Remote account", name,
        "Someone tried to create a Tailorly Remote account with this email address, but you already have one.",
        "Sign in", link,
        "If you forgot your password, use \"Forgot password?\" on the sign-in page. If this was not you, you can ignore this email.");

    public static EmailMessage ResetPassword(string to, string? name, string link) => Build(to,
        "Reset your Tailorly Remote password", name,
        "We received a request to reset your password. The link works for 1 hour.",
        "Choose a new password", link,
        "If you did not ask for this, you can ignore this email; your password stays the same.");

    public static EmailMessage ConfirmDeletion(string to, string? name, string link) => Build(to,
        "Confirm deleting your Tailorly Remote account", name,
        "You asked us to delete your account. After you confirm, we delete your résumés, tailored documents, tracker " +
        "and preferences. This cannot be undone. The link works for 1 hour.",
        "Delete my account", link,
        "If you did not ask for this, ignore this email and consider changing your password.");

    private static EmailMessage Build(string to, string subject, string? name, string body, string action, string link, string footer)
    {
        var e = (Func<string, string>)WebUtility.HtmlEncode;
        var html = $"""
            <!doctype html><html><body style="font-family:Arial,sans-serif;color:#16150f;line-height:1.5">
            <p>{e(Greeting(name))}</p>
            <p>{e(body)}</p>
            <p><a href="{e(link)}" style="background:#0e7a53;color:#fff;padding:10px 16px;border-radius:8px;text-decoration:none">{e(action)}</a></p>
            <p style="color:#6a675c;font-size:13px">Or paste this link into your browser:<br>{e(link)}</p>
            <p style="color:#6a675c;font-size:13px">{e(footer)}</p>
            <p style="color:#6a675c;font-size:13px">Tailorly Remote</p>
            </body></html>
            """;
        var text = $"{Greeting(name)}\n\n{body}\n\n{action}: {link}\n\n{footer}\n\nTailorly Remote\n";
        return new EmailMessage(to, subject, html, text);
    }
}
