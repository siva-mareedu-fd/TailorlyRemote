using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tailorly.App.Features.Accounts;
using Tailorly.App.Infrastructure;
using Tailorly.App.Infrastructure.Data;
using Tailorly.App.Infrastructure.Storage;
using Tailorly.Core.Ports;
using Tailorly.Core.Time;

namespace Tailorly.App.Features.Admin;

/// <summary>Thresholds for the admin email alerts (SDD §15.2). Section <c>Alerts</c>.</summary>
public sealed class AlertOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Recipients (comma-separated). Empty: every Admin with a verified email.</summary>
    public string? To { get; set; }

    /// <summary>While a condition lasts, the same alert is sent again at most this often.</summary>
    public int RepeatHours { get; set; } = 6;

    public int SourceConsecutiveFailures { get; set; } = 3;
    public int DigestUnsentMinutes { get; set; } = 60;
    public double AiErrorRatePercent { get; set; } = 10;
    public int AiErrorWindowMinutes { get; set; } = 15;

    /// <summary>Fewer calls than this in the window never alert (one failure in two calls is not a spike).</summary>
    public int AiErrorMinCalls { get; set; } = 5;

    public int DiskUsedPercent { get; set; } = 80;

    /// <summary>JSON written by <c>deploy/backup.sh</c> (<c>{"status":"ok|failed","at":…,"step":…}</c>). Empty: no backup check.</summary>
    public string? BackupStatusPath { get; set; }

    public int BackupMaxAgeHours { get; set; } = 26;

    /// <summary>Host name whose certificate is checked (SNI), e.g. the public domain. Empty: no certificate check.</summary>
    public string? CertificateHost { get; set; }

    /// <summary>Where to connect for the check, <c>host:port</c> (Compose: <c>caddy:443</c>). Default: the host on 443.</summary>
    public string? CertificateEndpoint { get; set; }

    public int CertificateMinDays { get; set; } = 14;
}

/// <summary>A condition that needs the admin. <see cref="Key"/> spaces out repeats; lines hold counts and ids only.</summary>
public sealed record ActiveAlert(string Key, string Title, IReadOnlyList<string> Lines);

/// <summary>
/// Evaluates the alert conditions of SDD §15.2 from data the app already keeps: <c>source_health</c>, <c>digests</c>,
/// <c>ai_usage</c>, the disk, the backup script's status file and the site's TLS certificate.
/// </summary>
public sealed class AlertChecks(
    AppDbContext db,
    SystemQueries system,
    IConfiguration config,
    IOptions<AlertOptions> options,
    IOptions<StorageOptions> storage,
    TimeProvider clock,
    ILogger<AlertChecks> logger)
{
    public async Task<List<ActiveAlert>> EvaluateAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var alerts = new List<ActiveAlert>();

        var failing = await (from s in db.JobSources.AsNoTracking()
                             join h in db.SourceHealth.AsNoTracking() on s.Key equals h.SourceKey
                             where s.Enabled && h.ConsecutiveFailures >= o.SourceConsecutiveFailures
                             select new { s.Key, s.DisplayName, h.ConsecutiveFailures, h.LastError, h.LastSuccessAt }).ToListAsync(ct);
        foreach (var f in failing)
            alerts.Add(new ActiveAlert($"source.failing:{f.Key}", $"Source {f.DisplayName} failed {f.ConsecutiveFailures} times in a row",
            [
                $"Source: {f.Key}",
                $"Last error code: {f.LastError ?? "unknown"}",
                $"Last success: {Format(f.LastSuccessAt)}",
                "Other sources keep running. Check Admin → Status, then the source's API or terms.",
            ]));

        var failedDigests = await system.FailedScheduledDigestsSinceAsync(now.AddHours(-1), ct);
        if (failedDigests > 0)
            alerts.Add(new ActiveAlert("digest.failed", $"{failedDigests} scheduled digest run(s) failed in the last hour",
                ["Users affected get no digest today unless a retry succeeds. Check Admin → Status and the job dashboard."]));
        var stats = await system.DigestRunStatsAsync(now.AddHours(-24), now.AddMinutes(-o.DigestUnsentMinutes), ct);
        if (stats.Unsent > 0)
            alerts.Add(new ActiveAlert("digest.unsent", $"{stats.Unsent} digest email(s) unsent for over {o.DigestUnsentMinutes} minutes",
                ["The runs finished but the email job has not sent them. Check the email provider and the job dashboard."]));

        var since = now.AddMinutes(-o.AiErrorWindowMinutes);
        var ai = await db.AiUsage.AsNoTracking().Where(u => u.CreatedAt >= since)
            .GroupBy(_ => 1).Select(g => new { Calls = g.Count(), Errors = g.Count(x => !x.Success) }).FirstOrDefaultAsync(ct);
        if (ai is { } a && a.Calls >= o.AiErrorMinCalls && 100.0 * a.Errors / a.Calls > o.AiErrorRatePercent)
            alerts.Add(new ActiveAlert("ai.error_rate",
                string.Create(CultureInfo.InvariantCulture, $"AI error rate {100.0 * a.Errors / a.Calls:0.#}% over {o.AiErrorWindowMinutes} minutes"),
                [$"{a.Errors} of {a.Calls} model calls failed (provider errors or invalid replies). Admin → Status lists them by task."]));

        if (config.GetValue<long?>("Ai:PlatformDailyEnrichmentTokenCeiling") is { } ceiling)
        {
            var india = TimeZones.RequireIana(TimeZones.India);
            var spent = await system.PlatformEnrichmentTokensSinceAsync(PeriodKey.DayStartUtc(now, india), ct);
            if (spent >= ceiling)
                alerts.Add(new ActiveAlert($"ai.platform_ceiling:{TimeZoneInfo.ConvertTime(now, india):yyyy-MM-dd}",
                    "Platform enrichment token ceiling reached",
                    [string.Create(CultureInfo.InvariantCulture, $"{spent:N0} of {ceiling:N0} job_enrichment tokens today (Asia/Kolkata)."),
                     "Runs now rank un-enriched jobs by rules until midnight IST. Raise Ai__PlatformDailyEnrichmentTokenCeiling if this is normal."]));
        }

        if (AdminReportsEndpoints.Disk(storage.Value.RootPath) is { } disk && disk.UsedPercent > o.DiskUsedPercent)
            alerts.Add(new ActiveAlert("disk.usage", $"Disk {disk.UsedPercent}% used",
                [string.Create(CultureInfo.InvariantCulture, $"{disk.FreeBytes / 1_073_741_824.0:0.0} GB free of {disk.TotalBytes / 1_073_741_824.0:0.0} GB."),
                 "See the operator runbook, \"Disk full\"."]));

        if (!string.IsNullOrWhiteSpace(o.BackupStatusPath) && Backup(o.BackupStatusPath, o.BackupMaxAgeHours, now) is { } backup)
            alerts.Add(backup);

        if (!string.IsNullOrWhiteSpace(o.CertificateHost) && await CertificateAsync(o, now, ct) is { } cert)
            alerts.Add(cert);

        return alerts;
    }

    private static ActiveAlert? Backup(string path, int maxAgeHours, DateTimeOffset now)
    {
        BackupStatus? status = null;
        try
        {
            if (File.Exists(path)) status = JsonSerializer.Deserialize<BackupStatus>(File.ReadAllText(path), JsonSerializerOptions.Web);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            status = new BackupStatus("unreadable", null, null, null);
        }
        if (status is null)
            return new ActiveAlert("backup.missing", "No backup recorded",
                [$"{path} does not exist. Run deploy/backup.sh once and check its cron entry."]);
        if (status.Status != "ok")
            return new ActiveAlert("backup.failed", "Nightly backup failed",
                [$"Status: {status.Status}; step: {status.Step ?? "unknown"}; at {Format(status.At)}.", "Run deploy/backup.sh by hand and read its output."]);
        if (status.LastSuccessAt is { } ok && ok < now.AddHours(-maxAgeHours) || status.At is { } at && at < now.AddHours(-maxAgeHours))
            return new ActiveAlert("backup.stale", $"No successful backup for over {maxAgeHours} hours",
                [$"Last success: {Format(status.LastSuccessAt ?? status.At)}. Check the backup cron entry on the VM."]);
        return null;
    }

    private async Task<ActiveAlert?> CertificateAsync(AlertOptions o, DateTimeOffset now, CancellationToken ct)
    {
        var host = o.CertificateHost!.Trim();
        var (connectHost, port) = Endpoint(o.CertificateEndpoint, host);
        X509Certificate2? cert = null;
        var errors = SslPolicyErrors.None;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(connectHost, port, timeout.Token);
            await using var ssl = new SslStream(tcp.GetStream(), false, (_, c, _, e) =>
            {
                if (c is not null) cert = new X509Certificate2(c);
                errors = e;
                return true; // read the certificate whatever its state; the state is reported below
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            if (ct.IsCancellationRequested) throw;
            AlertLog.CertificateCheckFailed(logger, ex.GetType().Name);
            return new ActiveAlert("certificate.unreachable", $"TLS certificate of {host} could not be checked",
                [$"Connecting to {connectHost}:{port} failed ({ex.GetType().Name}). Is Caddy running?"]);
        }
        using (cert)
        {
            if (cert is null)
                return new ActiveAlert("certificate.missing", $"{host} served no TLS certificate", ["Check Caddy's logs."]);
            var expires = new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            var days = (int)Math.Floor((expires - now).TotalDays);
            if (errors != SslPolicyErrors.None || days < o.CertificateMinDays)
                return new ActiveAlert("certificate.expiry", days < 0 ? $"TLS certificate of {host} has expired"
                        : $"TLS certificate of {host} expires in {days} day(s)",
                    [$"Expires {Format(expires)}; validation: {errors}.", "Caddy renews certificates by itself: check its logs and that ports 80/443 are reachable."]);
        }
        return null;
    }

    private static (string Host, int Port) Endpoint(string? endpoint, string fallbackHost)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return (fallbackHost, 443);
        var parts = endpoint.Trim().Split(':');
        return parts.Length == 2 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var port) ? (parts[0], port) : (parts[0], 443);
    }

    private static string Format(DateTimeOffset? at) =>
        at is { } t ? t.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "never";

    private sealed record BackupStatus(string Status, DateTimeOffset? At, string? Step, DateTimeOffset? LastSuccessAt);
}

/// <summary>
/// <c>AdminAlerts</c> (every 5 minutes): emails the admin about each active condition, then the same condition again
/// only after <see cref="AlertOptions.RepeatHours"/>. The last send is the newest <c>alert.sent</c> row in
/// <c>audit_log</c> for the alert key, so no new table is needed and the history is auditable. Emails hold counts,
/// ids and error codes only.
/// </summary>
public sealed class AdminAlertsJob(
    AlertChecks checks,
    AppDbContext db,
    UserManager<AppUser> users,
    IEmailSender email,
    AuditWriter audit,
    AppLinks links,
    IOptions<AlertOptions> options,
    TimeProvider clock,
    ILogger<AdminAlertsJob> logger)
{
    public const string RecurringId = "admin-alerts";

    [DisableConcurrentExecution(timeoutInSeconds: 240)]
    [AutomaticRetry(Attempts = 0)] // the next run is five minutes away
    public async Task RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled) return;
        var active = await checks.EvaluateAsync(ct);
        if (active.Count == 0) return;

        var now = clock.GetUtcNow();
        var keys = active.Select(a => a.Key).ToList();
        var lastSent = await db.AuditLog.AsNoTracking()
            .Where(a => a.Action == AuditActions.AlertSent && a.EntityId != null && keys.Contains(a.EntityId))
            .GroupBy(a => a.EntityId!).Select(g => new { g.Key, At = g.Max(a => a.At) })
            .ToDictionaryAsync(x => x.Key, x => x.At, ct);
        var due = active.Where(a => !lastSent.TryGetValue(a.Key, out var at) || at < now.AddHours(-o.RepeatHours)).ToList();
        if (due.Count == 0) return;

        var to = await RecipientsAsync(o);
        if (to.Count == 0)
        {
            AlertLog.NoRecipients(logger, due.Count);
            return;
        }
        foreach (var alert in due)
        {
            var (text, html) = Body(alert, links.Admin(), now);
            foreach (var address in to)
                await email.SendAsync(new EmailMessage(address, $"[Tailorly alert] {alert.Title}", html, text), ct);
            await audit.WriteAsync(AuditActions.AlertSent, null, "alert", alert.Key, new { recipients = to.Count }, ct);
            AlertLog.Sent(logger, alert.Key, to.Count);
        }
    }

    private async Task<List<string>> RecipientsAsync(AlertOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.To))
            return [.. o.To.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        return [.. (await users.GetUsersInRoleAsync(AppRole.Admin)).Where(u => u.EmailConfirmed && u.Email is not null).Select(u => u.Email!)];
    }

    internal static (string Text, string Html) Body(ActiveAlert alert, string statusUrl, DateTimeOffset now)
    {
        var lines = new List<string> { alert.Title, "" };
        lines.AddRange(alert.Lines);
        lines.Add("");
        lines.Add($"Admin console: {statusUrl}");
        lines.Add($"Checked at {now.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}. " +
                  "You get this again in a few hours if it is still the case.");
        var html = new StringBuilder("<div style=\"font-family:sans-serif;font-size:14px\">");
        foreach (var l in lines) html.Append("<p style=\"margin:0 0 6px\">").Append(HtmlEncoder.Default.Encode(l)).Append("</p>");
        html.Append("</div>");
        return (string.Join('\n', lines), html.ToString());
    }
}

internal static partial class AlertLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin alert {AlertKey} sent to {RecipientCount} recipient(s)")]
    public static partial void Sent(ILogger logger, string alertKey, int recipientCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "{AlertCount} admin alert(s) due but no recipient: set Alerts__To or verify an Admin")]
    public static partial void NoRecipients(ILogger logger, int alertCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate check failed with {ExceptionType}")]
    public static partial void CertificateCheckFailed(ILogger logger, string exceptionType);
}
