using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Features.Admin;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.SourcesTestData;

namespace Tailorly.Tests.Integration;

/// <summary>
/// Admin email alerts (SDD §15.2): each condition is raised from real rows, sent once, and not repeated within
/// <c>Alerts:RepeatHours</c>; emails carry counts, ids and codes only. Its own factory: fixed recipient, a backup status
/// file, a certificate endpoint that refuses connections, a tiny enrichment ceiling.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AdminAlertsTests(PostgresAppFixture fx) : IAsyncLifetime
{
    private const string Ops = "ops@example.test";
    private readonly string _backupStatus = Path.Combine(Path.GetTempPath(), $"tailorly-backup-{Guid.NewGuid():N}.json");
    private TailorlyAppFactory _f = null!;
    private string Cs => fx.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        _f = new TailorlyAppFactory(Cs, new Dictionary<string, string?>
        {
            ["Alerts:To"] = Ops,
            ["Alerts:BackupStatusPath"] = _backupStatus,
            ["Alerts:CertificateHost"] = "tailorly.test",
            ["Alerts:CertificateEndpoint"] = "127.0.0.1:1",
            ["Ai:PlatformDailyEnrichmentTokenCeiling"] = "100",
            ["Alerts:DiskUsedPercent"] = "100", // the test machine's disk must not decide; see Disk_usage_over_the_threshold_alerts
        });
        await ResetAsync(_f, Cs);
        await CleanAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await CleanAsync();
        await ResetAsync(fx.Factory, Cs);
        File.Delete(_backupStatus);
        await _f.DisposeAsync();
    }

    private Task CleanAsync() => Db.ExecAsync(Cs, """
        DELETE FROM audit_log WHERE action = 'alert.sent';
        DELETE FROM ai_usage WHERE task IN ('alert_test', 'job_enrichment') AND user_id IS NULL;
        DELETE FROM digests WHERE error_code = 'ALERT_TEST';
        """);

    private async Task<List<string>> RunAsync()
    {
        var before = _f.Emails.Sent.Count;
        await using var scope = _f.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AdminAlertsJob>().RunAsync(CancellationToken.None);
        return _f.Emails.Sent.Skip(before).Select(m => m.Subject).ToList();
    }

    private Task WriteBackupStatusAsync(string status, DateTimeOffset at) =>
        File.WriteAllTextAsync(_backupStatus, $$"""{"status":"{{status}}","at":"{{at:O}}","step":"upload"}""",
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Every_condition_alerts_once_and_then_waits_for_the_repeat_interval()
    {
        await WriteBackupStatusAsync("failed", DateTimeOffset.UtcNow);
        await Db.ExecAsync(Cs, "UPDATE source_health SET consecutive_failures = 3, last_error = 'HTTP_503' WHERE source_key = 'remoteok'");
        await Db.ExecAsync(Cs, """
            INSERT INTO ai_usage (user_id, task, model, bucket, period_key, success, error_code, created_at)
            SELECT NULL, 'alert_test', 'm', 'platform', '2026-11', false, 'AI_UNAVAILABLE', now()
            FROM generate_series(1, 200) g;
            INSERT INTO ai_usage (user_id, task, model, bucket, period_key, counted_tokens, success, created_at)
            VALUES (NULL, 'job_enrichment', 'm', 'platform', '2026-11', 150, true, now());
            """);
        var user = await TestUsers.CreateVerifiedAsync(_f, "alerted");
        await Db.ExecAsync(Cs, """
            INSERT INTO digests (id, user_id, kind, local_date, status, error_code, finished_at)
            VALUES (gen_random_uuid(), @u, 'scheduled', current_date, 'failed', 'ALERT_TEST', now())
            """, ("u", user.Id));

        var subjects = await RunAsync();
        subjects.Should().Contain(s => s.Contains("Source Remote OK failed 3 times in a row", StringComparison.Ordinal));
        subjects.Should().Contain(s => s.Contains("scheduled digest run(s) failed in the last hour", StringComparison.Ordinal));
        // 200 failures outweigh any calls other tests made in the same 15 minutes.
        subjects.Should().Contain(s => s.Contains("AI error rate", StringComparison.Ordinal) && s.Contains("over 15 minutes", StringComparison.Ordinal));
        subjects.Should().Contain(s => s.Contains("Platform enrichment token ceiling reached", StringComparison.Ordinal));
        subjects.Should().Contain(s => s.Contains("Nightly backup failed", StringComparison.Ordinal));
        subjects.Should().Contain(s => s.Contains("TLS certificate of tailorly.test could not be checked", StringComparison.Ordinal));
        subjects.Should().OnlyContain(s => s.StartsWith("[Tailorly alert] ", StringComparison.Ordinal));
        var alerts = _f.Emails.Sent.Where(m => m.Subject.StartsWith("[Tailorly alert]", StringComparison.Ordinal)).ToList();
        alerts.Should().OnlyContain(m => m.To == Ops, "Alerts:To overrides the admin list");
        alerts.Should().OnlyContain(m => !m.TextBody.Contains(user.Email, StringComparison.OrdinalIgnoreCase)
                                       && !m.TextBody.Contains("@", StringComparison.Ordinal), "alerts carry no user data");

        (await RunAsync()).Should().BeEmpty("the same conditions are not sent again within the repeat interval");
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM audit_log WHERE action = 'alert.sent'")).Should().Be(subjects.Count);

        await Db.ExecAsync(Cs, "UPDATE audit_log SET at = now() - interval '7 hours' WHERE action = 'alert.sent'");
        (await RunAsync()).Should().Contain(s => s.Contains("Remote OK", StringComparison.Ordinal), "a lasting condition repeats after 6 hours");
    }

    [Fact]
    public async Task Healthy_state_sends_nothing_and_a_disabled_source_never_alerts()
    {
        await WriteBackupStatusAsync("ok", DateTimeOffset.UtcNow.AddHours(-2));
        await Db.ExecAsync(Cs, "UPDATE source_health SET consecutive_failures = 9 WHERE source_key = 'himalayas'"); // disabled
        await Db.ExecAsync(Cs, """
            INSERT INTO ai_usage (user_id, task, model, bucket, period_key, success, created_at)
            SELECT NULL, 'alert_test', 'm', 'platform', '2026-11', g <> 1, now() FROM generate_series(1, 20) g;
            """); // 5 % errors
        var subjects = await RunAsync();
        subjects.Should().NotContain(s => s.Contains("Source", StringComparison.Ordinal) || s.Contains("AI error", StringComparison.Ordinal)
            || s.Contains("backup", StringComparison.OrdinalIgnoreCase) || s.Contains("ceiling", StringComparison.Ordinal)
            || s.Contains("Disk", StringComparison.Ordinal));
        subjects.Should().Contain(s => s.Contains("TLS certificate", StringComparison.Ordinal), "the unreachable endpoint still alerts");
    }

    [Fact]
    public async Task Disk_usage_over_the_threshold_alerts()
    {
        await using var f = new TailorlyAppFactory(Cs, new Dictionary<string, string?> { ["Alerts:To"] = Ops, ["Alerts:DiskUsedPercent"] = "-1" });
        await using var scope = f.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AdminAlertsJob>().RunAsync(CancellationToken.None);
        f.Emails.Sent.Should().Contain(m => m.Subject.Contains("% used", StringComparison.Ordinal)
            && m.TextBody.Contains("GB free of", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_old_successful_backup_or_no_status_file_alerts()
    {
        await WriteBackupStatusAsync("ok", DateTimeOffset.UtcNow.AddHours(-30));
        (await RunAsync()).Should().Contain(s => s.Contains("No successful backup for over 26 hours", StringComparison.Ordinal));
        File.Delete(_backupStatus);
        (await RunAsync()).Should().Contain(s => s.Contains("No backup recorded", StringComparison.Ordinal));
    }
}
