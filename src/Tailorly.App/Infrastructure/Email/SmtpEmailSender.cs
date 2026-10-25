using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Tailorly.Core.Ports;

namespace Tailorly.App.Infrastructure.Email;

public sealed class SmtpOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary><c>None</c> for local Mailpit; <c>StartTls</c> or <c>SslOnConnect</c> in production.</summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTlsWhenAvailable;
}

public sealed class EmailOptions
{
    public string Provider { get; set; } = "Smtp";
    public string From { get; set; } = "Tailorly Remote <no-reply@localhost>";
    public SmtpOptions Smtp { get; set; } = new();
}

/// <summary>SMTP sender (Mailpit locally). Never logs recipients or bodies.</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(o.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        if (message.ListUnsubscribeUrl is { } url)
        {
            mime.Headers.Add("List-Unsubscribe", $"<{url}>");
            mime.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
        }
        mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(o.Smtp.Host, o.Smtp.Port, o.Smtp.Security, ct);
        if (!string.IsNullOrEmpty(o.Smtp.Username))
            await client.AuthenticateAsync(o.Smtp.Username, o.Smtp.Password ?? string.Empty, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
