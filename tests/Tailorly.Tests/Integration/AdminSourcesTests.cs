using System.Net;
using System.Text.Json;
using FluentAssertions;
using Tailorly.App.Features.Sources;
using Tailorly.App.Features.Sources.Adapters;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.SourcesTestData;

namespace Tailorly.Tests.Integration;

/// <summary>Admin sources, companies, coverage and status (ApiDesign §4.11, PRD F15).</summary>
[Collection(PostgresCollection.Name)]
public sealed class AdminSourcesTests(PostgresAppFixture fx) : IAsyncLifetime
{
    private TailorlyAppFactory F => fx.Factory;
    private string Cs => fx.ConnectionString;

    public async ValueTask InitializeAsync() => await ResetAsync(F, Cs);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static JsonElement Source(JsonElement list, string key) => list.EnumerateArray().Single(s => s.GetProperty("key").GetString() == key);

    [Fact]
    public async Task Sources_show_flags_health_and_whether_an_adapter_exists()
    {
        var admin = await TestUsers.CreateAdminAsync(F);
        var list = await ApiClient.JsonAsync(await admin.Api.GetAsync("/api/v1/admin/sources"));
        list.GetArrayLength().Should().Be(8);
        var him = Source(list, "himalayas");
        (him.GetProperty("enabled").GetBoolean(), him.GetProperty("requireSourceApplyUrl").GetBoolean(),
            him.GetProperty("forbidThirdPartyResolution").GetBoolean(), him.GetProperty("purgeOnExpiry").GetBoolean(),
            him.GetProperty("pollIntervalMinutes").GetInt32(), him.GetProperty("hasAdapter").GetBoolean())
            .Should().Be((false, true, true, true, 1440, true));
        Source(list, "remoteok").GetProperty("termsReviewedAt").GetString().Should().StartWith("2026-10-25");
        Source(list, "jobicy").GetProperty("hasAdapter").GetBoolean().Should().BeFalse();
        Source(list, "wwr").GetProperty("hasAdapter").GetBoolean().Should().BeFalse();
        Source(list, "greenhouse").GetProperty("health").GetProperty("consecutiveFailures").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_source_cannot_be_enabled_before_its_terms_are_reviewed()
    {
        var admin = await TestUsers.CreateAdminAsync(F);
        var refused = await admin.Api.PutAsync("/api/v1/admin/sources/himalayas", new { enabled = true });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.CodeAsync(refused)).Should().Be("TERMS_NOT_REVIEWED");

        var ok = await admin.Api.PutAsync("/api/v1/admin/sources/himalayas",
            new { enabled = true, termsReviewedAt = "2026-10-26T09:00:00Z", pollIntervalMinutes = 720 });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(ok));
        var body = await ApiClient.JsonAsync(ok);
        (body.GetProperty("enabled").GetBoolean(), body.GetProperty("pollIntervalMinutes").GetInt32()).Should().Be((true, 720));

        (await ApiClient.CodeAsync(await admin.Api.PutAsync("/api/v1/admin/sources/himalayas", new { clearTermsReview = true })))
            .Should().Be("TERMS_NOT_REVIEWED", "an enabled source keeps its review");
        var off = await ApiClient.JsonAsync(await admin.Api.PutAsync("/api/v1/admin/sources/himalayas", new { enabled = false, clearTermsReview = true }));
        (off.GetProperty("enabled").GetBoolean(), off.GetProperty("termsReviewedAt").ValueKind).Should().Be((false, JsonValueKind.Null));

        (await ApiClient.CodeAsync(await admin.Api.PutAsync("/api/v1/admin/sources/remoteok", new { pollIntervalMinutes = 1 })))
            .Should().Be("VALIDATION_FAILED");
        (await admin.Api.PutAsync("/api/v1/admin/sources/nope", new { enabled = false })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Run_now_enqueues_one_isolated_fetch()
    {
        var admin = await TestUsers.CreateAdminAsync(F);
        (await ApiClient.CodeAsync(await admin.Api.PostAsync("/api/v1/admin/sources/himalayas/run"))).Should().Be("VALIDATION_FAILED");
        (await ApiClient.CodeAsync(await admin.Api.PostAsync("/api/v1/admin/sources/jobicy/run"))).Should().Be("VALIDATION_FAILED");

        F.Jobs.Clear();
        F.Http.AlwaysFixture(RemoteOkUrl, "remoteok/api.json");
        var res = await admin.Api.PostAsync("/api/v1/admin/sources/remoteok/run");
        res.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await ApiClient.JsonAsync(res)).GetProperty("runId").GetString().Should().NotBeNullOrEmpty();
        await F.Jobs.RunAllAsync();
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM jobs WHERE source_key = 'remoteok'")).Should().Be(11);
    }

    [Fact]
    public async Task Seed_list_loads_idempotently_and_queues_verification()
    {
        SeedLoader.ReadEmbedded().Should().HaveCountGreaterThanOrEqualTo(100).And
            .OnlyContain(e => Tailorly.Core.Jobs.AtsUrl.IsValidToken(e.BoardToken) && Tailorly.Core.Jobs.AtsUrl.IsPlatform(e.Platform));
        var admin = await TestUsers.CreateAdminAsync(F);
        F.Jobs.Clear();

        var first = await ApiClient.JsonAsync(await admin.Api.PostAsync("/api/v1/admin/companies/seed/load"));
        var added = first.GetProperty("added").GetInt32();
        added.Should().Be(SeedLoader.ReadEmbedded().Count);
        F.Jobs.History.Should().Contain(j => j.Type == typeof(VerifyLinksJob));
        F.Jobs.Clear(); // the verify job would call 169 boards

        var second = await ApiClient.JsonAsync(await admin.Api.PostAsync("/api/v1/admin/companies/seed/load"));
        (second.GetProperty("added").GetInt32(), second.GetProperty("skipped").GetInt32()).Should().Be((0, added));

        var gh = await ApiClient.JsonAsync(await admin.Api.GetAsync("/api/v1/admin/companies?platform=greenhouse&origin=seed&pageSize=5"));
        gh.GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(100, "the Greenhouse seed alone covers the ≥ 100 boards of M2");
        gh.GetProperty("items").GetArrayLength().Should().Be(5);
        var search = await ApiClient.JsonAsync(await admin.Api.GetAsync("/api/v1/admin/companies?q=gitlab"));
        search.GetProperty("items").EnumerateArray().Should().ContainSingle(i => i.GetProperty("boardToken").GetString() == "gitlab");
    }

    [Fact]
    public async Task Add_override_and_verify_a_board()
    {
        var admin = await TestUsers.CreateAdminAsync(F);
        F.Http.AlwaysFixture(GreenhouseApi + "gitlab/jobs", "greenhouse/gitlab_jobs.json");
        var created = await admin.Api.PostAsync("/api/v1/admin/companies",
            new { name = "GitLab", websiteDomain = "https://www.gitlab.com/", platform = "greenhouse", boardToken = "gitlab" });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await ApiClient.TextAsync(created));
        var link = await ApiClient.JsonAsync(created);
        (link.GetProperty("origin").GetString(), link.GetProperty("openJobsCount").GetInt32(), link.GetProperty("websiteDomain").GetString())
            .Should().Be(("admin", 5, "gitlab.com"));
        var id = link.GetProperty("id").GetGuid();

        var blocked = await ApiClient.JsonAsync(await admin.Api.PutAsync($"/api/v1/admin/companies/{id}", new { status = "blocked" }));
        blocked.GetProperty("status").GetString().Should().Be("blocked");
        (await ApiClient.CodeAsync(await admin.Api.PutAsync($"/api/v1/admin/companies/{id}", new { status = "gone" }))).Should().Be("VALIDATION_FAILED");

        F.Http.Always(GreenhouseApi + "gitlab/jobs", 404, Fixtures.Read("greenhouse/board_not_found.json"));
        var verified = await ApiClient.JsonAsync(await admin.Api.PostAsync($"/api/v1/admin/companies/{id}/verify"));
        (verified.GetProperty("status").GetString(), verified.GetProperty("failureCount").GetInt32()).Should().Be(("blocked", 1),
            "an admin block is not overridden by a check");

        F.Http.Always(GreenhouseApi + "nosuch/jobs", 404, Fixtures.Read("greenhouse/board_not_found.json"));
        (await ApiClient.CodeAsync(await admin.Api.PostAsync("/api/v1/admin/companies",
            new { name = "No Such", platform = "greenhouse", boardToken = "nosuch" }))).Should().Be("BOARD_NOT_FOUND");
        (await ApiClient.CodeAsync(await admin.Api.PostAsync("/api/v1/admin/companies",
            new { name = "Bad", platform = "greenhouse", boardToken = "../etc" }))).Should().Be("VALIDATION_FAILED");
    }

    [Fact]
    public async Task Coverage_report_counts_india_open_jobs_per_day_by_source_and_role()
    {
        await EnableAsync(Cs, HimalayasSource.Key);
        await SetLastSuccessAsync(Cs, HimalayasSource.Key, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        F.Http.AlwaysFixture(HimalayasUrl(HimalayasSource.IndiaStream, 1), "himalayas/search_country_in_excl_ww_p1.json")
            .Always(HimalayasUrl(HimalayasSource.IndiaStream, 2), 200, """{"jobs":[]}""")
            .AlwaysFixture(HimalayasUrl(HimalayasSource.WorldwideStream, 1), "himalayas/search_worldwide_p1.json")
            .Always(HimalayasUrl(HimalayasSource.WorldwideStream, 2), 200, """{"jobs":[]}""")
            .AlwaysFixture(RemoteOkUrl, "remoteok/api.json");
        await FetchAsync(F, HimalayasSource.Key, manual: true);
        await FetchAsync(F, RemoteOkSource.Key);
        await Db.ExecAsync(Cs, "UPDATE jobs SET title = 'Senior Backend Engineer' WHERE external_id = (SELECT min(external_id) FROM jobs WHERE source_key = 'himalayas')");

        var admin = await TestUsers.CreateAdminAsync(F);
        var report = await ApiClient.JsonAsync(await admin.Api.GetAsync("/api/v1/admin/coverage"));
        report.GetProperty("basis").GetString().Should().Be("rules");
        report.GetProperty("days").GetArrayLength().Should().Be(7);
        report.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("backend", "frontend", "data", "devops", "mobile");
        report.GetProperty("total").GetInt32().Should().Be(23);
        var him = report.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("sourceKey").GetString() == "himalayas");
        (him.GetProperty("worldwide").GetInt32(), him.GetProperty("indiaEligible").GetInt32(), him.GetProperty("openToIndia").GetInt32())
            .Should().Be((6, 6, 12));
        report.GetProperty("openToIndia").GetInt32().Should().BeGreaterThanOrEqualTo(12);
        report.GetProperty("roleTotals").GetProperty("backend").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        var today = report.GetProperty("days").EnumerateArray().Last();
        today.GetProperty("total").GetInt32().Should().Be(23);

        var custom = await ApiClient.JsonAsync(await admin.Api.GetAsync("/api/v1/admin/coverage?from=2026-10-20&to=2026-10-10&roles=Engineer,%20sales"));
        (custom.GetProperty("from").GetString(), custom.GetProperty("to").GetString()).Should().Be(("2026-10-10", "2026-10-20"));
        custom.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("engineer", "sales");
    }

    [Fact]
    public async Task Status_page_and_every_admin_call_is_audited()
    {
        var admin = await TestUsers.CreateAdminAsync(F);
        var before = await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM audit_log WHERE action LIKE 'admin.%'");
        var status = await admin.Api.GetAsync("/api/v1/admin/status");
        status.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(status));
        var body = await ApiClient.JsonAsync(status);
        body.GetProperty("sources").GetArrayLength().Should().Be(7, "the 'byo' pseudo-source is not fetched");
        body.GetProperty("jobs").GetProperty("total").GetInt32().Should().Be(0);
        body.GetProperty("stuckResources").GetInt32().Should().BeGreaterThanOrEqualTo(0);

        await admin.Api.PutAsync("/api/v1/admin/sources/himalayas", new { enabled = true }); // refused, still audited
        var rows = await Db.StringsAsync(Cs, """
            SELECT action || ' ' || entity || ' ' || coalesce(entity_id, '-') || ' ' || (meta->>'outcome')
            FROM audit_log WHERE action LIKE 'admin.%' AND actor_user_id = @u ORDER BY id
            """, ("u", admin.Id));
        rows.Should().Contain("admin.get /api/v1/admin/status - ok").And.Contain("admin.put /api/v1/admin/sources/{key} himalayas TERMS_NOT_REVIEWED");
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM audit_log WHERE action LIKE 'admin.%'")).Should().Be(before + 2);
    }

    [Fact]
    public async Task Non_admins_are_refused()
    {
        var user = await TestUsers.CreateVerifiedAsync(F, "not-admin");
        foreach (var path in new[] { "/api/v1/admin/sources", "/api/v1/admin/coverage", "/api/v1/admin/status", "/api/v1/admin/companies" })
            (await user.Api.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
        (await user.Api.PostAsync("/api/v1/admin/companies/seed/load")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
