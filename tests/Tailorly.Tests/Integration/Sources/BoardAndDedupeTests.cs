using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Features.Sources;
using Tailorly.App.Features.Sources.Adapters;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.SourcesTestData;

namespace Tailorly.Tests.Integration.Sources;

/// <summary>Path B polling, cross-source dedupe (PRD §7.1a/§7.4) and company linking stage 1 (PRD §7.3).</summary>
[Collection(PostgresCollection.Name)]
public sealed class BoardAndDedupeTests(PostgresAppFixture fx) : IAsyncLifetime
{
    private TailorlyAppFactory F => fx.Factory;
    private string Cs => fx.ConnectionString;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await ResetAsync(F, Cs);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<long> Count(string sql) => Db.ScalarAsync<long>(Cs, sql)!;

    private void Board(string token, string company, params (long Id, string Title, string Location)[] jobs)
    {
        F.Http.Always(GreenhouseApi + token + "/jobs", 200, GreenhouseList(company, jobs));
        foreach (var j in jobs)
            F.Http.Always($"{GreenhouseApi}{token}/jobs/{j.Id}", 200, GreenhouseJob(company, j.Id, j.Title, j.Location));
    }

    [Fact]
    public async Task Boards_list_first_fetch_details_only_for_new_ids_and_close_removed_jobs()
    {
        await AddLinkAsync(F, "greenhouse", "acmecloud", "Acme Cloud");
        Board("acmecloud", "Acme Cloud", (1, "Backend Engineer", "Remote, India"), (2, "Data Engineer", "Bengaluru, India"),
            (3, "Account Executive", "Remote, France"));

        var first = await FetchAsync(F, "greenhouse");
        first!.New.Should().Be(3);
        first.Requests.Should().Be(4, "one list call and one detail call per new job");
        (await Count("SELECT count(*) FROM jobs WHERE source_key = 'greenhouse' AND description_completeness = 'full' AND company_id IS NOT NULL"))
            .Should().Be(3);
        (await Db.StringsAsync(Cs, "SELECT external_id FROM jobs ORDER BY 1")).Should().Equal("acmecloud:1", "acmecloud:2", "acmecloud:3");

        // Second poll: nothing new, so only the list is read.
        F.Http.Requests.Clear();
        var second = await FetchAsync(F, "greenhouse", manual: true);
        (second!.New, second.Requests).Should().Be((0, 1));

        // Job 3 leaves the board: closed (and hidden), not deleted.
        Board("acmecloud", "Acme Cloud", (1, "Backend Engineer", "Remote, India"), (2, "Data Engineer", "Bengaluru, India"));
        var third = await FetchAsync(F, "greenhouse", manual: true);
        third!.Closed.Should().Be(1);
        (await Count("SELECT count(*) FROM jobs WHERE external_id = 'acmecloud:3' AND closed_at IS NOT NULL")).Should().Be(1);
        (await Count("SELECT open_jobs_count FROM company_ats_links WHERE board_token = 'acmecloud'")).Should().Be(2);
    }

    [Fact]
    public async Task A_big_new_board_is_read_once_with_content_instead_of_one_call_per_job()
    {
        await AddLinkAsync(F, "greenhouse", "bigco", "BigCo");
        var jobs = Enumerable.Range(1, 30).Select(i => ((long)i, $"Engineer {i}", "Remote")).ToArray();
        F.Http.Always(GreenhouseApi + "bigco/jobs", 200, GreenhouseList("BigCo", jobs));
        var withContent = System.Text.Json.JsonSerializer.Serialize(new
        {
            jobs = jobs.Select(j => System.Text.Json.JsonDocument.Parse(GreenhouseJob("BigCo", j.Item1, j.Item2, j.Item3)).RootElement),
        });
        F.Http.Always(GreenhouseApi + "bigco/jobs?content=true", 200, withContent);

        var stats = await FetchAsync(F, "greenhouse");
        (stats!.New, stats.Requests).Should().Be((30, 2));
        F.Http.CountPrefix(GreenhouseApi + "bigco/jobs/").Should().Be(0);
    }

    [Fact]
    public async Task A_missing_board_is_marked_not_found_retried_weekly_and_never_throws()
    {
        await AddLinkAsync(F, "greenhouse", "gone", "Gone Inc");
        await AddLinkAsync(F, "greenhouse", "alive", "Alive");
        F.Http.Always(GreenhouseApi + "gone/jobs", 404, Fixtures.Read("greenhouse/board_not_found.json"));
        Board("alive", "Alive", (7, "SRE", "Remote"));

        var stats = await FetchAsync(F, "greenhouse");
        (stats!.BoardsNotFound, stats.New).Should().Be((1, 1));
        var row = await Db.StringsAsync(Cs, "SELECT status || '|' || failure_count FROM company_ats_links WHERE board_token = 'gone'");
        row.Should().Equal("not_found|1");
        var retry = await Db.ScalarAsync<DateTime>(Cs, "SELECT next_retry_at FROM company_ats_links WHERE board_token = 'gone'");
        retry.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(5));
        (await Count("SELECT count(*) FROM source_health WHERE source_key = 'greenhouse' AND last_error IS NULL")).Should().Be(1);

        // Not polled again before the retry date.
        F.Http.Requests.Clear();
        await FetchAsync(F, "greenhouse", manual: true);
        F.Http.Count(GreenhouseApi + "gone/jobs").Should().Be(0);
    }

    [Fact]
    public async Task The_ats_record_suppresses_the_himalayas_copy_and_each_keeps_its_own_apply_link()
    {
        await EnableAsync(Cs, HimalayasSource.Key);
        await SetLastSuccessAsync(Cs, HimalayasSource.Key, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        // The first real job of the India page, also posted on its employer's Greenhouse board.
        var himalayasJob = System.Text.Json.JsonDocument.Parse(Fixtures.Read("himalayas/search_country_in_excl_ww_p1.json"))
            .RootElement.GetProperty("jobs")[0];
        var title = himalayasJob.GetProperty("title").GetString()!;
        var company = himalayasJob.GetProperty("companyName").GetString()!;
        F.Http.AlwaysFixture(HimalayasUrl(HimalayasSource.IndiaStream, 1), "himalayas/search_country_in_excl_ww_p1.json")
            .Always(HimalayasUrl(HimalayasSource.IndiaStream, 2), 200, """{"jobs":[]}""")
            .Always(HimalayasUrl(HimalayasSource.WorldwideStream, 1), 200, """{"jobs":[]}""");
        await AddLinkAsync(F, "greenhouse", "theemployer", company);
        Board("theemployer", company, (555, title + " (Remote)", "Remote, India"));

        await FetchAsync(F, HimalayasSource.Key, manual: true);
        await FetchAsync(F, "greenhouse");

        var rows = await Db.StringsAsync(Cs, """
            SELECT j.source_key || '|' || (j.duplicate_of_job_id IS NULL) || '|' || j.apply_url
            FROM jobs j WHERE j.dedupe_key = (SELECT dedupe_key FROM jobs WHERE source_key = 'greenhouse') ORDER BY 1
            """);
        rows.Should().HaveCount(2);
        rows[0].Should().StartWith("greenhouse|true|https://job-boards.greenhouse.io/");
        rows[1].Should().StartWith("himalayas|false|https://himalayas.app/").And.Contain("utm_source=api",
            "the Himalayas row keeps its applicationLink; it is only suppressed");

        // The board closes the job: the Himalayas listing becomes the visible one again.
        Board("theemployer", company);
        F.Http.Always(GreenhouseApi + "theemployer/jobs", 200, GreenhouseList(company));
        await FetchAsync(F, "greenhouse", manual: true);
        (await Count("SELECT count(*) FROM jobs WHERE source_key = 'himalayas' AND duplicate_of_job_id IS NULL AND title = '" +
                     title.Replace("'", "''") + "'")).Should().Be(1);
    }

    [Fact]
    public async Task Among_feeds_the_full_description_wins_and_duplicates_point_at_it()
    {
        await using var scope = F.Services.CreateAsyncScope();
        var ingest = scope.ServiceProvider.GetRequiredService<JobIngestor>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var remoteok = db.JobSources.Single(s => s.Key == "remoteok");
        var jobicy = db.JobSources.Single(s => s.Key == "jobicy");

        Tailorly.Core.Ports.RawJob Raw(string source, string id, string text) => new()
        {
            SourceKey = source, ExternalId = id, Title = "Senior Backend Engineer", CompanyName = "Kiln Labs",
            ApplyUrl = $"https://{source}.example/apply/{id}", SourceUrl = $"https://{source}.example/{id}", DescriptionHtml = text,
        };
        await ingest.IngestAsync(remoteok, [Raw("remoteok", "r1", "<p>short teaser</p>")], null, Ct);
        await ingest.IngestAsync(jobicy, [Raw("jobicy", "j1", "<p>" + new string('x', 800) + "</p>")], null, Ct);

        (await Db.StringsAsync(Cs, "SELECT source_key || '|' || (duplicate_of_job_id IS NULL) FROM jobs ORDER BY 1"))
            .Should().Equal("jobicy|true", "remoteok|false");
    }

    [Fact]
    public async Task Ats_links_are_discovered_from_feed_apply_urls_but_never_from_himalayas()
    {
        await using var scope = F.Services.CreateAsyncScope();
        var ingest = scope.ServiceProvider.GetRequiredService<JobIngestor>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Tailorly.Core.Ports.RawJob Raw(string source, string id, string apply, string html = "") => new()
        {
            SourceKey = source, ExternalId = id, Title = "Platform Engineer", CompanyName = "Lumen Systems",
            ApplyUrl = apply, SourceUrl = $"https://{source}.example/{id}", DescriptionHtml = html,
        };

        var fromRemoteOk = await ingest.IngestAsync(db.JobSources.Single(s => s.Key == "remoteok"),
            [Raw("remoteok", "r9", "https://job-boards.greenhouse.io/lumensystems/jobs/42",
                "<p>Or apply at <a href=\"https://jobs.lever.co/lumen/abc\">Lever</a></p>")], null, Ct);
        fromRemoteOk.DiscoveredLinks.Should().HaveCount(2);
        (await Db.StringsAsync(Cs, "SELECT platform || ':' || board_token || ':' || origin || ':' || status FROM company_ats_links ORDER BY 1"))
            .Should().Equal("greenhouse:lumensystems:discovered:resolved", "lever:lumen:discovered:resolved");
        (await Db.StringsAsync(Cs, "SELECT apply_url FROM jobs WHERE external_id = 'r9'"))
            .Should().Equal(["https://job-boards.greenhouse.io/lumensystems/jobs/42"], "the source's apply URL is stored as given");

        await Db.ExecAsync(Cs, "TRUNCATE company_ats_links, companies CASCADE");
        var fromHimalayas = await ingest.IngestAsync(db.JobSources.Single(s => s.Key == "himalayas"),
            [Raw("himalayas", "h9", "https://boards.greenhouse.io/lumensystems/jobs/43")], null, Ct);
        fromHimalayas.DiscoveredLinks.Should().BeEmpty("forbid_third_party_resolution: no ATS lookup for Himalayas jobs");
        (await Count("SELECT count(*) FROM company_ats_links")).Should().Be(0);
        (await Db.StringsAsync(Cs, "SELECT apply_url FROM jobs WHERE external_id = 'h9'"))
            .Should().Equal("https://boards.greenhouse.io/lumensystems/jobs/43");
    }

    [Fact]
    public async Task Feed_jobs_link_to_a_known_company_by_name_and_a_discovered_board_is_polled_eagerly()
    {
        await AddLinkAsync(F, "greenhouse", "acmecloud", "Acme Cloud, Inc.");
        F.Http.Always(RemoteOkUrl, 200, $$"""
            [{"legal":"x"},{"id":"77","position":"QA Engineer","company":"ACME CLOUD","url":"https://remoteok.com/remote-jobs/77",
              "apply_url":"https://job-boards.greenhouse.io/newco/jobs/9","epoch":1792800002,"description":"<p>Test</p>"}]
            """);
        await FetchAsync(F, "remoteok");
        (await Count("SELECT count(*) FROM jobs j JOIN companies c ON c.id = j.company_id WHERE j.external_id = '77' AND c.normalized_name = 'acme cloud'"))
            .Should().Be(1);
        F.Jobs.History.Should().Contain(j => j.Type == typeof(PollBoardJob), "a newly discovered board is polled right away");
    }
}
