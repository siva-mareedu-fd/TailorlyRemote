using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Tailorly.App.Features.Digests;

/// <summary>
/// Signed, purpose-bound one-click unsubscribe tokens for digest emails (SDD §11.5, ApiDesign §4.10). The payload is
/// only the user id. They do not expire, so an old email's link still works; the only thing they can do is turn the
/// daily digest off.
/// </summary>
public sealed class UnsubscribeTokens(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Tailorly.DigestUnsubscribe.v1");

    public string Create(Guid userId) => _protector.Protect(userId.ToString("D"));

    public Guid? Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2000) return null;
        try
        {
            return Guid.TryParse(_protector.Unprotect(token), out var id) ? id : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// Signed 24-hour links that confirm a notification email different from the sign-in email (proposal 18). The payload
/// binds the user id and the address, so a link for an address the user has since replaced does nothing.
/// </summary>
public sealed class NotificationEmailTokens(IDataProtectionProvider provider, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly ITimeLimitedDataProtector _protector =
        provider.CreateProtector("Tailorly.NotificationEmail.v1").ToTimeLimitedDataProtector();

    public string Create(Guid userId, string email) =>
        _protector.Protect($"{userId:D}|{email.Trim().ToLowerInvariant()}", clock.GetUtcNow().Add(Lifetime));

    public (Guid UserId, string Email)? Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2000) return null;
        try
        {
            var payload = _protector.Unprotect(token, out var expires);
            if (expires <= clock.GetUtcNow()) return null;
            var parts = payload.Split('|', 2);
            return parts.Length == 2 && Guid.TryParse(parts[0], out var id) ? (id, parts[1]) : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
