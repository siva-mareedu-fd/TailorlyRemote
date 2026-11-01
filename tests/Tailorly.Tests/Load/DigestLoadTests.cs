using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Infrastructure.Ai;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Ports;
using Tailorly.Tests.Infrastructure;
using Testcontainers.PostgreSql;
using static Tailorly.Tests.Infrastructure.MatchingTestData;

namespace Tailorly.Tests.Load;

/// <summary>
/// Load and soak test of the daily digest (M6; SDD §16.1–16.2): many users on the same schedule slot, one dispatcher tick,
/// then the real Hangfire server (production worker count) and the real AI concurrency gate work through the per-user
/// runs: enrichment on demand (shared), up to 20 fit scores per user, the digest and its email. The model is the fake
/// provider behind a per-call latency, so the run measures our pipeline under the AI API's pace, not the model.
/// Writes a report to <c>tests/load-reports/</c>. Opt-in (it runs for minutes):
/// <code>
/// TAILORLY_LOAD_TEST=1 dotnet test --project tests/Tailorly.Tests --filter-class "*DigestLoadTests"
///   # TAILORLY_LOAD_USERS=100 TAILORLY_LOAD_JOBS=150 TAILORLY_LOAD_LATENCY_MS=2000 TAILORLY_LOAD_WORKERS=4
///   # TAILORLY_LOAD_AI_CONCURRENCY=4 TAILORLY_LOAD_TIMEOUT_MIN=60
/// </code>
/// Its own PostgreSQL container: the dispatcher sees every user in the database.
/// </summary>
public sealed class DigestLoadTests
{
    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The fake provider behind a delay (±25 % jitter), counting calls and the peak of calls in flight.</summary>
    private sealed class LatencyModelProvider(FakeModelProvider inner, int latencyMs) : IModelProvider
    {
        private int _inFlight;
        public int PeakInFlight;
        public readonly ConcurrentDictionary<string, int> Calls = new();
        public long BusyMs;

        public async Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int peak;
            while (now > (peak = Volatile.Read(ref PeakInFlight)) && Interlocked.CompareExchange(ref PeakInFlight, now, peak) != peak) { }
            Calls.AddOrUpdate(request.Task, 1, (_, n) => n + 1);
            var delay = latencyMs <= 0 ? 0 : (int)(latencyMs * (0.75 + Random.Shared.NextDouble() * 0.5));
            try
            {
                if (delay > 0) await Task.Delay(delay, ct);
                Interlocked.Add(ref BusyMs, delay);
                return await inner.SendAsync(request, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    [Fact]
    public async Task Users_on_the_same_slot_all_get_their_digest()
    {
        if (Environment.GetEnvironmentVariable("TAILORLY_LOAD_TEST") != "1")
            Assert.Skip("Set TAILORLY_LOAD_TEST=1 to run the digest load test (several minutes).");

        var userCount = Env("TAILORLY_LOAD_USERS", 100);
        var jobCount = Env("TAILORLY_LOAD_JOBS", 150);
        var latency = Env("TAILORLY_LOAD_LATENCY_MS", 2000);
        var workers = Env("TAILORLY_LOAD_WORKERS", 4);
        var aiConcurrency = Env("TAILORLY_LOAD_AI_CONCURRENCY", 4);
        var timeout = TimeSpan.FromMinutes(Env("TAILORLY_LOAD_TIMEOUT_MIN", 60));

        await using var pg = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase("tailorly").WithUsername("tailorly")
            .WithPassword("tailorly-load").Build();
        await pg.StartAsync(Ct);
        var cs = pg.GetConnectionString();
        // Migrate first with a factory that has no job server: the load factory's Hangfire server needs the schema at start.
        await using (var migrator = new TailorlyAppFactory(cs))
        await using (var scope = migrator.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MigrationRunner>().MigrateAsync(Ct);
        LatencyModelProvider? model = null;
        await using var f = new TailorlyAppFactory(cs, new Dictionary<string, string?>
        {
            ["Hangfire:ServerEnabled"] = "true",
            ["Hangfire:WorkerCount"] = workers.ToString(CultureInfo.InvariantCulture),
            ["Ai:MaxConcurrentCalls"] = aiConcurrency.ToString(CultureInfo.InvariantCulture),
            ["Ai:PlatformDailyEnrichmentTokenCeiling"] = "",
        }, services: s =>
        {
            // The real Hangfire client instead of the inline one, and the fake model behind a latency.
            s.AddSingleton<IBackgroundJobClient>(sp => new BackgroundJobClient(sp.GetRequiredService<JobStorage>()));
            s.AddSingleton<IModelProvider>(sp => model = new LatencyModelProvider(sp.GetRequiredService<FakeModelProvider>(), latency));
        });
        _ = f.Server; // starts the host and with it the Hangfire server

        // ---- arrange: users on one slot (their local "now", Asia/Kolkata), and fresh jobs everyone can match ----
        var india = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var slot = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, india).AddMinutes(-1).ToString("HH:mm", CultureInfo.InvariantCulture);
        var seed = Stopwatch.StartNew();
        var users = new ConcurrentBag<Guid>();
        await Parallel.ForEachAsync(Enumerable.Range(0, userCount), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Ct },
            async (i, _) => users.Add(await SeedUserAsync(f, i, slot)));
        // The seeding parses ran through the latency too; measure the digest slot only.
        await WaitAsync(cs, "SELECT count(*) FROM resumes WHERE parse_status = 'ready'", userCount, TimeSpan.FromMinutes(30));
        for (var i = 0; i < jobCount; i++)
            await InsertJobAsync(f, $"Backend Engineer {i}", company: $"Load Co {i % 40}");
        var seeded = seed.Elapsed;
        model!.Calls.Clear();
        Interlocked.Exchange(ref model.BusyMs, 0);
        Interlocked.Exchange(ref model.PeakInFlight, 0);
        var emailsBefore = f.Emails.Sent.Count;

        // ---- act: one dispatcher tick claims every user; the Hangfire server does the rest ----
        var clock = Stopwatch.StartNew();
        var claimed = await DispatchAsync(f, DateTimeOffset.UtcNow);
        claimed.Should().HaveCount(userCount, "every user is due in this slot");
        var firstDone = TimeSpan.Zero;
        while (true)
        {
            var done = await Db.ScalarAsync<long>(cs,
                "SELECT count(*) FROM digests WHERE kind = 'scheduled' AND (status = 'failed' OR emailed_at IS NOT NULL OR (status = 'ready' AND NOT EXISTS (SELECT 1 FROM digest_items i WHERE i.digest_id = digests.id)))");
            if (done > 0 && firstDone == TimeSpan.Zero) firstDone = clock.Elapsed;
            if (done >= userCount) break;
            clock.Elapsed.Should().BeLessThan(timeout, $"{done} of {userCount} digests finished");
            await Task.Delay(1000, Ct);
        }
        var total = clock.Elapsed;
        var second = await DispatchAsync(f, DateTimeOffset.UtcNow);

        // ---- report ----
        var runs = (await Db.StringsAsync(cs, """
            SELECT concat_ws(',', extract(epoch FROM started_at - created_at)::numeric(10,1), extract(epoch FROM finished_at - started_at)::numeric(10,1),
                             status, degraded, coalesce(jobs_scored, 0), coalesce(error_code, ''), attempt)
            FROM digests WHERE kind = 'scheduled'
            """)).Select(r => r.Split(',')).ToList();
        var wait = runs.Select(r => double.Parse(r[0], CultureInfo.InvariantCulture)).Order().ToList();
        var duration = runs.Select(r => double.Parse(r[1], CultureInfo.InvariantCulture)).Order().ToList();
        var failed = runs.Count(r => r[2] == "failed");
        var degraded = runs.Count(r => r[3] == "t");
        var scored = runs.Sum(r => int.Parse(r[4], CultureInfo.InvariantCulture));
        var retried = runs.Count(r => int.Parse(r[6], CultureInfo.InvariantCulture) > 1);
        var enrichments = await Db.ScalarAsync<long>(cs, "SELECT count(*) FROM job_enrichment");
        var calls = model.Calls.ToDictionary(kv => kv.Key, kv => kv.Value);
        var totalCalls = calls.Values.Sum();
        var floor = TimeSpan.FromMilliseconds((double)Interlocked.Read(ref model.BusyMs) / aiConcurrency);
        var emails = f.Emails.Sent.Count - emailsBefore;
        var peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1_048_576;

        static double P(List<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(p * sorted.Count) - 1)];
        var inv = CultureInfo.InvariantCulture;
        var report = new StringBuilder();
        report.AppendLine(inv, $"# Digest load test, {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC").AppendLine();
        report.AppendLine(inv, $"{userCount} users on the same slot ({slot} IST), {jobCount} new jobs, model latency {latency} ms ±25 %, " +
                               $"Hangfire workers {workers}, AI concurrency {aiConcurrency} (production defaults: 4 and 4).").AppendLine();
        report.AppendLine("| Measure | Value |").AppendLine("|---|---|");
        report.AppendLine(inv, $"| **All digests done (dispatch → last email)** | **{total:mm\\:ss}** |");
        report.AppendLine(inv, $"| First digest done | {firstDone:mm\\:ss} |");
        report.AppendLine(inv, $"| Digests ready / failed / degraded / retried | {userCount - failed} / {failed} / {degraded} / {retried} |");
        report.AppendLine(inv, $"| Digest emails sent | {emails} |");
        report.AppendLine(inv, $"| Wait in queue before the run started (p50 / p95 / max) | {P(wait, .5):0}s / {P(wait, .95):0}s / {wait.LastOrDefault():0}s |");
        report.AppendLine(inv, $"| Run time per user (p50 / p95 / max) | {P(duration, .5):0}s / {P(duration, .95):0}s / {duration.LastOrDefault():0}s |");
        report.AppendLine(inv, $"| Model calls ({string.Join(", ", calls.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value}"))}) | {totalCalls} |");
        report.AppendLine(inv, $"| Jobs scored (sum over users) / enrichments created | {scored} / {enrichments} |");
        report.AppendLine(inv, $"| Peak model calls in flight | {model.PeakInFlight} (limit {aiConcurrency}) |");
        report.AppendLine(inv, $"| Model-bound floor (Σ latency ÷ AI concurrency) | {floor:mm\\:ss} ({floor.TotalSeconds / total.TotalSeconds:P0} of the total) |");
        report.AppendLine(inv, $"| Second dispatcher tick in the same slot | {second.Count} claimed |");
        report.AppendLine(inv, $"| Test process peak working set | {peakMb} MB |");
        report.AppendLine(inv, $"| Seeding (users, résumés, jobs; not part of the slot) | {seeded:mm\\:ss} |");
        var path = ReportPath();
        await File.WriteAllTextAsync(path, report.ToString(), Ct);
        TestContext.Current.SendDiagnosticMessage(report.ToString());
        TestContext.Current.SendDiagnosticMessage($"Report: {path}");

        failed.Should().Be(0);
        second.Should().BeEmpty("one scheduled digest per user per local day, also under load");
        emails.Should().BeGreaterThan(0);
    }

    private static async Task<Guid> SeedUserAsync(TailorlyAppFactory f, int i, string slot)
    {
        var api = await f.CreateApiClientAsync();
        var email = $"load{i}-{Guid.NewGuid():N}@example.test";
        var res = await api.PostAsync("/api/v1/auth/register", new
        {
            email, password = TestUsers.Password, displayName = $"Load {i}", timeZone = "Asia/Kolkata", consentVersion = TestUsers.ConsentVersion,
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await ApiClient.TextAsync(res));
        Guid id;
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            (await users.ConfirmEmailAsync(user, await users.GenerateEmailConfirmationTokenAsync(user))).Succeeded.Should().BeTrue();
            id = user.Id;
        }
        await TestUsers.LoginAsync(api, email);
        await api.RefreshCsrfAsync();
        (await api.UploadAsync("/api/v1/resumes", "resume.docx", DocxBuilder.SampleResume())).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var u = new TestUser(api, id, email, TestUsers.Password);
        await SetPreferencesAsync(u, p =>
        {
            p["targetRoles"] = new[] { "Backend Engineer" };
            p["scheduleEnabled"] = true;
            p["scheduleLocalTime"] = slot;
            p["minMatchScore"] = 1;
        });
        return id;
    }

    private static async Task WaitAsync(string cs, string countSql, long expected, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (await Db.ScalarAsync<long>(cs, countSql) < expected)
        {
            sw.Elapsed.Should().BeLessThan(timeout, countSql);
            await Task.Delay(500, Ct);
        }
    }

    private static string ReportPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Tailorly.sln"))) dir = Path.GetDirectoryName(dir);
        var reports = Path.Combine(dir ?? Path.GetTempPath(), "tests", "load-reports");
        Directory.CreateDirectory(reports);
        return Path.Combine(reports, $"digest-load-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.md");
    }
}
