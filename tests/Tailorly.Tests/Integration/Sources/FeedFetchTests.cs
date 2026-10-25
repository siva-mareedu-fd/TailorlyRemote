using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Features.Sources;
using Tailorly.App.Features.Sources.Adapters;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.SourcesTestData;

namespace Tailorly.Tests.Integration.Sources;

/// <summary>Path A end to end: fixtures served by the fake HTTP handler, the real runner, normaliser, dedupe and database.</summary>
[Collection(PostgresCollection.Name)]
public sealed class FeedFetchTests(PostgresAppFixture fx) : IAsyncLifetime
{
    private TailorlyAppFactory F => fx.Factory;
    private string Cs => fx.ConnectionString;
    private static readonly DateTimeOffset BeforeFixtures = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await ResetAsync(F, Cs);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<long> Count(string sql, params (string, object?)[] args) => Db.ScalarAsync<long>(Cs, sql, args)!;

    [Fact]
    public async Task RemoteOk_fetch_fills_jobs_with_attribution_and_no_text_in_logs()
    {
        F.Http.AlwaysFixture(RemoteOkUrl, "remoteok/api.json");
        var stats = await FetchAsync(F, RemoteOkSource.Key);

        stats!.New.Should().Be(11);
        stats.Requests.Should().Be(1);
        (await Count("SELECT count(*) FROM jobs WHERE source_key = 'remoteok'")).Should().Be(11);
        (await Count("""
            SELECT count(*) FROM jobs WHERE source_key = 'remoteok'
              AND source_url LIKE 'https://remoteOK.com/remote-jobs/%' AND apply_url LIKE 'https://remoteOK.com/%'
              AND work_mode = 'remote' AND description_text NOT LIKE '%<%' AND description_text NOT LIKE '%Please mention the word%'
            """)).Should().Be(11);
        (await Count("SELECT count(*) FROM jobs WHERE source_key = 'remoteok' AND description_completeness = 'limited'"))
            .Should().BeGreaterThan(0, "Remote OK teasers end in '...'");
        (await Count("SELECT count(*) FROM jobs WHERE description_text LIKE '%â€%' OR description_text LIKE '%Ã%'"))
            .Should().Be(0, "double-encoded UTF-8 is repaired");

        var health = await Db.StringsAsync(Cs, "SELECT last_run_stats::text FROM source_health WHERE source_key = 'remoteok'");
        JsonDocument.Parse(health[0]).RootElement.GetProperty("new").GetInt32().Should().Be(11);
        (await Count("SELECT count(*) FROM source_health WHERE source_key = 'remoteok' AND last_success_at IS NOT NULL AND calls_this_month = 1"))
            .Should().Be(1);
        F.Logs.Lines.Should().NotContain(l => l.Contains("Consequential Work", StringComparison.Ordinal), "job text is never logged");
    }

    [Fact]
    public async Task A_second_run_makes_one_request_per_stream_and_adds_nothing()
    {
        await EnableAsync(Cs, HimalayasSource.Key);
        await SetLastSuccessAsync(Cs, HimalayasSource.Key, BeforeFixtures);
        F.Http.AlwaysFixture(HimalayasUrl(HimalayasSource.IndiaStream, 1), "himalayas/search_country_in_excl_ww_p1.json")
            .Always(HimalayasUrl(HimalayasSource.IndiaStream, 2), 200, """{"jobs":[]}""")
            .AlwaysFixture(HimalayasUrl(HimalayasSource.WorldwideStream, 1), "himalayas/search_worldwide_p1.json")
            .Always(HimalayasUrl(HimalayasSource.WorldwideStream, 2), 200, """{"jobs":[]}""");
        F.Http.AlwaysFixture(RemoteOkUrl, "remoteok/api.json");

        var first = await FetchAsync(F, HimalayasSource.Key, manual: true);
        first!.New.Should().Be(12);
        first.Requests.Should().Be(4, "each stream reads page 1, then page 2 is empty");
        (await FetchAsync(F, RemoteOkSource.Key))!.New.Should().Be(11);
        F.Http.Requests.Clear();

        var second = await FetchAsync(F, HimalayasSource.Key, manual: true);
        second!.New.Should().Be(0);
        second.Requests.Should().Be(2, "page 1 of each stream is all known, so the walk stops");
        second.Stops.Values.Should().OnlyContain(s => s == "all_known" || s == "caught_up", "page 1 is known and older than the last run");
        var remoteOk = await FetchAsync(F, RemoteOkSource.Key);
        (remoteOk!.New, remoteOk.Requests).Should().Be((0, 1));
        (await Count("SELECT count(*) FROM jobs")).Should().Be(23);

        // Structured eligibility and the Himalayas apply-link rule survive storage.
        (await Count("""
            SELECT count(*) FROM jobs WHERE source_key = 'himalayas'
              AND apply_url LIKE 'https://himalayas.app/%utm_source=api%' AND expires_at > posted_at
              AND (source_meta->>'isWorldwide')::boolean = (location_class = 'remote-worldwide')
            """)).Should().Be(12);
    }

    [Fact]
    public async Task The_walk_stops_at_jobs_older_than_since_and_saves_a_cursor_at_the_page_cap()
    {
        await EnableAsync(Cs, HimalayasSource.Key);
        F.Http.AlwaysFixture(HimalayasUrl(HimalayasSource.IndiaStream, 1), "himalayas/search_country_in_p1.json")
            .AlwaysFixture(HimalayasUrl(HimalayasSource.IndiaStream, 2), "himalayas/search_country_in_p2.json")
            .Always(HimalayasUrl(HimalayasSource.IndiaStream, 3), 200, """{"jobs":[]}""")
            .AlwaysFixture(HimalayasUrl(HimalayasSource.WorldwideStream, 1), "himalayas/search_worldwide_p1.json");

        // since after every fixture job: page 1's last job is older → caught up after one page per stream.
        await SetLastSuccessAsync(Cs, HimalayasSource.Key, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var caughtUp = await FetchAsync(F, HimalayasSource.Key, manual: true);
        caughtUp!.Stops.Values.Should().OnlyContain(s => s == "caught_up");
        caughtUp.Requests.Should().Be(2);

        // Page cap of 1 with an old since: the India stream stops at the cap and its cursor is saved.
        await ResetAsync(F, Cs);
        await EnableAsync(Cs, HimalayasSource.Key);
        F.Http.AlwaysFixture(HimalayasUrl(HimalayasSource.IndiaStream, 1), "himalayas/search_country_in_p1.json")
            .AlwaysFixture(HimalayasUrl(HimalayasSource.IndiaStream, 2), "himalayas/search_country_in_p2.json")
            .Always(HimalayasUrl(HimalayasSource.IndiaStream, 3), 200, """{"jobs":[]}""")
            .AlwaysFixture(HimalayasUrl(HimalayasSource.WorldwideStream, 1), "himalayas/search_worldwide_p1.json")
            .Always(HimalayasUrl(HimalayasSource.WorldwideStream, 2), 200, """{"jobs":[]}""");
        await SetLastSuccessAsync(Cs, HimalayasSource.Key, BeforeFixtures);
        await using var scope = F.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        FeedRunner Runner(int pages) => ActivatorUtilities.CreateInstance<FeedRunner>(sp,
            Microsoft.Extensions.Options.Options.Create(new FetchOptions { MaxPagesPerRun = pages }));
        var adapter = ActivatorUtilities.CreateInstance<HimalayasSource>(sp);
        var db = sp.GetRequiredService<Tailorly.App.Infrastructure.Data.AppDbContext>();
        var source = db.JobSources.Single(s => s.Key == HimalayasSource.Key);
        var health = db.SourceHealth.Single(h => h.SourceKey == HimalayasSource.Key);
        var ct = TestContext.Current.CancellationToken;

        var stats = await Runner(1).RunAsync(source, health, adapter, ct);
        stats.Stops[HimalayasSource.IndiaStream].Should().Be("page_cap");
        FeedRunner.ReadCursors(health.LastCursor)[HimalayasSource.IndiaStream].Cursor.Should().Be("2");

        // The next run reads the top (all known), then resumes at the saved page with the pages left.
        F.Http.Requests.Clear();
        var resumed = await Runner(2).RunAsync(source, health, adapter, ct);
        resumed.Stops[HimalayasSource.IndiaStream].Should().StartWith("all_known+resume:");
        F.Http.Count(HimalayasUrl(HimalayasSource.IndiaStream, 2)).Should().Be(1);
    }

    [Theory]
    [InlineData(500, "Unavailable: HTTP 500", 3)]
    [InlineData(502, "Unavailable: HTTP 502", 3)]
    [InlineData(402, "Unavailable: HTTP 402", 1)]
    [InlineData(404, "NotFound: HTTP 404", 1)]
    public async Task Http_failures_are_recorded_retried_only_when_transient_and_never_thrown(int status, string error, int attempts)
    {
        F.Http.Always(RemoteOkUrl, status, "{}");
        var stats = await FetchAsync(F, RemoteOkSource.Key); // must not throw
        stats.Should().BeNull();
        F.Http.Count(RemoteOkUrl).Should().Be(attempts);
        var row = await Db.StringsAsync(Cs, "SELECT last_error || '|' || consecutive_failures FROM source_health WHERE source_key = 'remoteok'");
        row.Should().Equal($"{error}|1");
        (await Count("SELECT count(*) FROM source_health WHERE source_key = 'remoteok' AND circuit_open_until IS NULL")).Should().Be(1);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(403)]
    public async Task Throttling_opens_the_circuit_and_the_dispatcher_skips_the_source(int status)
    {
        F.Http.Always(RemoteOkUrl, status, "{}", headers: new Dictionary<string, string> { ["Retry-After"] = "7200" });
        await FetchAsync(F, RemoteOkSource.Key);
        F.Http.Count(RemoteOkUrl).Should().Be(1, "429/403 are not retried");
        var open = await Db.ScalarAsync<DateTime>(Cs, "SELECT circuit_open_until FROM source_health WHERE source_key = 'remoteok'");
        open.Should().BeCloseTo(DateTime.UtcNow.AddHours(2), TimeSpan.FromMinutes(2), "Retry-After (2 h) is longer than the 60-minute floor");

        var due = await InScopeAsync(F, sp => sp.GetRequiredService<FetchSourcesJob>().RunAsync(TestContext.Current.CancellationToken));
        due.Should().NotContain(RemoteOkSource.Key).And.Contain("greenhouse");
        (await FetchAsync(F, RemoteOkSource.Key)).Should().BeNull();
        F.Http.Count(RemoteOkUrl).Should().Be(1, "a scheduled run is skipped while the circuit is open");
    }

    [Fact]
    public async Task A_timeout_is_retried_then_recorded()
    {
        F.Http.Always(RemoteOkUrl, async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return FakeHttp.Build(200, "[]");
        });
        await FetchAsync(F, RemoteOkSource.Key);
        F.Http.Count(RemoteOkUrl).Should().Be(3);
        (await Db.StringsAsync(Cs, "SELECT last_error FROM source_health WHERE source_key = 'remoteok'")).Should().Equal("Unavailable: Timed out");
    }

    [Fact]
    public async Task Malformed_json_and_a_changed_schema_are_bad_payload()
    {
        F.Http.Always(RemoteOkUrl, 200, "[{\"legal\": \"x\"}, {\"id\": 1, ");
        await FetchAsync(F, RemoteOkSource.Key);
        (await Db.StringsAsync(Cs, "SELECT last_error FROM source_health WHERE source_key = 'remoteok'"))
            .Should().Equal("BadPayload: Response is not valid JSON");

        F.Http.Always(RemoteOkUrl, 200, """{"jobs": []}""");
        await FetchAsync(F, RemoteOkSource.Key);
        (await Db.StringsAsync(Cs, "SELECT last_error || '|' || consecutive_failures FROM source_health WHERE source_key = 'remoteok'"))
            .Should().Equal("BadPayload: Remote OK response is not an array|2");
    }

    [Fact]
    public async Task An_empty_feed_is_a_successful_run_with_nothing_new()
    {
        F.Http.Always(RemoteOkUrl, 200, """[{"last_updated": 1792857604, "legal": "API Terms of Service"}]""");
        var stats = await FetchAsync(F, RemoteOkSource.Key);
        (stats!.Fetched, stats.New).Should().Be((0, 0));
        (await Count("SELECT count(*) FROM source_health WHERE source_key = 'remoteok' AND last_success_at IS NOT NULL AND consecutive_failures = 0"))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_transient_error_then_success_is_one_successful_run()
    {
        F.Http.Once(RemoteOkUrl, 503, "busy").AlwaysFixture(RemoteOkUrl, "remoteok/api.json");
        (await FetchAsync(F, RemoteOkSource.Key))!.New.Should().Be(11);
        F.Http.Count(RemoteOkUrl).Should().Be(2);
    }

    [Fact]
    public async Task One_failing_source_never_stops_the_others_in_a_cycle()
    {
        await EnableAsync(Cs, HimalayasSource.Key);
        await SetLastSuccessAsync(Cs, HimalayasSource.Key, BeforeFixtures);
        F.Http.Always(RemoteOkUrl, 500, "down"); // killed source
        F.Http.Always(HimalayasUrl(HimalayasSource.IndiaStream, 1), 200, "{\"totally\": \"different schema\"}"); // changed schema
        var linkId = await AddLinkAsync(F, "greenhouse", "acmecloud", "Acme Cloud");
        F.Http.Always(GreenhouseApi + "acmecloud/jobs", 200, GreenhouseList("Acme Cloud", (101, "Backend Engineer", "Remote, India")))
            .Always(GreenhouseApi + "acmecloud/jobs/101", 200, GreenhouseJob("Acme Cloud", 101, "Backend Engineer", "Remote, India"));

        var due = await InScopeAsync(F, sp => sp.GetRequiredService<FetchSourcesJob>().RunAsync(TestContext.Current.CancellationToken));
        due.Should().BeEquivalentTo(["remoteok", "himalayas", "greenhouse"]);
        await F.Jobs.RunAllAsync(); // each source in its own job; none throws

        (await Count("SELECT count(*) FROM jobs WHERE source_key = 'greenhouse'")).Should().Be(1);
        (await Db.StringsAsync(Cs, "SELECT source_key || ':' || (last_error IS NULL) || ':' || consecutive_failures FROM source_health WHERE source_key IN ('remoteok','himalayas','greenhouse') ORDER BY 1"))
            .Should().Equal("greenhouse:true:0", "himalayas:false:1", "remoteok:false:1");
        linkId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Disabled_sources_are_never_fetched()
    {
        // Himalayas, Jobicy and WWR ship disabled (seed).
        (await Db.StringsAsync(Cs, "SELECT key FROM job_sources WHERE NOT enabled ORDER BY key")).Should()
            .Equal("ashby", "himalayas", "jobicy", "lever", "wwr");
        await SetLastSuccessAsync(Cs, HimalayasSource.Key, BeforeFixtures);
        var due = await InScopeAsync(F, sp => sp.GetRequiredService<FetchSourcesJob>().RunAsync(TestContext.Current.CancellationToken));
        due.Should().NotContain(HimalayasSource.Key);
        (await FetchAsync(F, HimalayasSource.Key, manual: true)).Should().BeNull();
        F.Http.CountPrefix("https://himalayas.app/").Should().Be(0);
    }

    [Fact]
    public async Task Expired_himalayas_jobs_are_purged_but_other_sources_keep_theirs()
    {
        await EnableAsync(Cs, HimalayasSource.Key);
        await SetLastSuccessAsync(Cs, HimalayasSource.Key, BeforeFixtures);
        F.Http.AlwaysFixture(HimalayasUrl(HimalayasSource.IndiaStream, 1), "himalayas/search_country_in_excl_ww_p1.json")
            .Always(HimalayasUrl(HimalayasSource.IndiaStream, 2), 200, """{"jobs":[]}""")
            .Always(HimalayasUrl(HimalayasSource.WorldwideStream, 1), 200, """{"jobs":[]}""")
            .AlwaysFixture(RemoteOkUrl, "remoteok/api.json");
        await FetchAsync(F, HimalayasSource.Key, manual: true);
        await FetchAsync(F, RemoteOkSource.Key);
        await Db.ExecAsync(Cs, "UPDATE jobs SET expires_at = now() - interval '1 minute'"); // every listing "expired"

        var purged = await InScopeAsync(F, sp => sp.GetRequiredService<RetentionJob>().PurgeExpiredAsync(TestContext.Current.CancellationToken));
        purged.Should().Be(6);
        (await Count("SELECT count(*) FROM jobs WHERE source_key = 'himalayas'")).Should().Be(0);
        (await Count("SELECT count(*) FROM jobs WHERE source_key = 'remoteok'")).Should().Be(11, "Remote OK has no purge_on_expiry");
    }

    [Fact]
    public async Task Retention_deletes_old_and_long_closed_listings()
    {
        F.Http.AlwaysFixture(RemoteOkUrl, "remoteok/api.json");
        await FetchAsync(F, RemoteOkSource.Key);
        await Db.ExecAsync(Cs, """
            UPDATE jobs SET posted_at = now() - interval '61 days' WHERE external_id = (SELECT min(external_id) FROM jobs);
            UPDATE jobs SET closed_at = now() - interval '8 days' WHERE external_id = (SELECT max(external_id) FROM jobs);
            """);
        var deleted = await InScopeAsync(F, sp => sp.GetRequiredService<RetentionJob>().RunDailyAsync(TestContext.Current.CancellationToken));
        deleted.Should().Be(2);
        (await Count("SELECT count(*) FROM jobs")).Should().Be(9);
    }


    [Fact]
    public async Task A_source_that_is_already_running_is_skipped_and_the_lock_is_released_after_a_run()
    {
        F.Http.AlwaysFixture(RemoteOkUrl, "remoteok/api.json");
        await using (var conn = new Npgsql.NpgsqlConnection(Cs))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = new Npgsql.NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended('fetch:remoteok', 0))", conn);
            await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            (await FetchAsync(F, RemoteOkSource.Key)).Should().BeNull("another run holds the source");
            F.Http.Count(RemoteOkUrl).Should().Be(0);
            await using var unlock = new Npgsql.NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended('fetch:remoteok', 0))", conn);
            await unlock.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }
        (await FetchAsync(F, RemoteOkSource.Key))!.New.Should().Be(11);
        (await FetchAsync(F, RemoteOkSource.Key, manual: true)).Should().NotBeNull("the previous run released its lock");
    }
}
