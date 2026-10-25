namespace Tailorly.Core.Ports;

/// <summary>Sends transactional email built from templates. Callers never pass user-controlled headers.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <param name="ListUnsubscribeUrl">When set, adds RFC 8058 one-click unsubscribe headers (digest emails).</param>
public sealed record EmailMessage(
    string To,
    string Subject,
    string HtmlBody,
    string TextBody,
    string? ListUnsubscribeUrl = null);
