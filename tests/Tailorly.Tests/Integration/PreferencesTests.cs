using System.Net;
using System.Text.Json;
using FluentAssertions;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.SourcesTestData;

namespace Tailorly.Tests.Integration;

/// <summary>Preferences and the watch list (PRD F3, ApiDesign §4.5).</summary>
[Collection(PostgresCollection.Name)]
public sealed class PreferencesTests(PostgresAppFixture fx) : IAsyncLifetime
{
    private TailorlyAppFactory F => fx.Factory;
    private string Cs => fx.ConnectionString;

    public async ValueTask InitializeAsync() => await ResetAsync(F, Cs);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Dictionary<string, object?> Valid(string token) => new()
    {
        ["searchScope"] = "india_plus_global_remote",
        ["targetRoles"] = new[] { "Backend engineer", ".NET developer", "backend ENGINEER", " " },
        ["locations"] = new[] { "Hyderabad" },
        ["workModes"] = new[] { "remote", "hybrid" },
        ["jobTypes"] = new[] { "full_time" },
        ["hiringModels"] = new[] { "employee", "contractor" },
        ["salaryMin"] = 3_000_000,
        ["salaryMax"] = 5_500_000,
        ["salaryCurrency"] = "INR",
        ["experienceLevel"] = null,
        ["minMatchScore"] = 7,
        ["includeUnspecifiedSalary"] = true,
        ["showUnclearEligibility"] = false,
        ["timezoneOverlap"] = "Up to 4h overlap with US East",
        ["excludedCompanies"] = new[] { "Fieldnote" },
        ["excludedKeywords"] = new[] { "unpaid" },
        ["scheduleEnabled"] = true,
        ["scheduleLocalTime"] = "19:30",
        ["digestWhenEmpty"] = false,
        ["concurrencyToken"] = token,
    };

    [Fact]
    public async Task Defaults_until_saved_then_full_replacement_with_a_concurrency_token()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "prefs");
        var defaults = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/preferences"));
        defaults.GetProperty("searchScope").GetString().Should().Be("india_plus_global_remote");
        defaults.GetProperty("minMatchScore").GetInt32().Should().Be(8);
        defaults.GetProperty("scheduleLocalTime").GetString().Should().Be("18:00");
        defaults.GetProperty("workModes").EnumerateArray().Select(x => x.GetString()).Should().Equal("remote");
        defaults.GetProperty("salaryCurrency").GetString().Should().Be("INR");
        defaults.GetProperty("concurrencyToken").GetString().Should().Be("0");

        var saved = await u.Api.PutAsync("/api/v1/preferences", Valid("0"));
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(saved));
        var body = await ApiClient.JsonAsync(saved);
        body.GetProperty("targetRoles").EnumerateArray().Select(x => x.GetString()).Should().Equal("Backend engineer", ".NET developer");
        body.GetProperty("scheduleLocalTime").GetString().Should().Be("19:30");
        var token = body.GetProperty("concurrencyToken").GetString()!;
        token.Should().NotBe("0");

        // A second tab still holding the old token loses.
        (await ApiClient.CodeAsync(await u.Api.PutAsync("/api/v1/preferences", Valid("0")))).Should().Be("CONCURRENT_UPDATE");
        var nine = Valid(token);
        nine["minMatchScore"] = 9;
        var again = await u.Api.PutAsync("/api/v1/preferences", nine);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiClient.CodeAsync(await u.Api.PutAsync("/api/v1/preferences", Valid(token)))).Should().Be("CONCURRENT_UPDATE");

        var reread = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/preferences"));
        reread.GetProperty("minMatchScore").GetInt32().Should().Be(9);
        reread.GetProperty("concurrencyToken").GetString().Should().Be((await ApiClient.JsonAsync(again)).GetProperty("concurrencyToken").GetString());
    }

    [Theory]
    [InlineData("salaryMax", 1_000)]
    [InlineData("minMatchScore", 0)]
    [InlineData("minMatchScore", 11)]
    [InlineData("scheduleLocalTime", "25:00")]
    [InlineData("scheduleLocalTime", "6pm")]
    [InlineData("searchScope", "everywhere")]
    [InlineData("salaryCurrency", "EUR")]
    [InlineData("workModes", "none")]
    [InlineData("jobTypes", "gig")]
    [InlineData("targetRoles", "toolong")]
    [InlineData("excludedKeywords", "toomany")]
    [InlineData("salaryMin", -1)]
    public async Task Validation_failures_name_the_field(string field, object value)
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "prefs-invalid");
        var body = Valid("0");
        body[field] = value switch
        {
            "none" => Array.Empty<string>(),
            "gig" => new[] { "gig" },
            "toolong" => new[] { new string('r', 101) },
            "toomany" => Enumerable.Range(0, 26).Select(i => $"kw{i}").ToArray(),
            _ => value,
        };
        var res = await u.Api.PutAsync("/api/v1/preferences", body);
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await ApiClient.JsonAsync(res);
        problem.GetProperty("code").GetString().Should().Be("VALIDATION_FAILED");
        problem.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue(problem.ToString());
    }

    // ---------- watch list ----------

    private const string GitlabApi = GreenhouseApi + "gitlab/jobs";

    [Fact]
    public async Task Adding_a_board_calls_only_the_fixed_api_url_and_is_idempotent()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "watch");
        F.Http.AlwaysFixture(GitlabApi, "greenhouse/gitlab_jobs.json");

        var res = await u.Api.PostAsync("/api/v1/watched-companies",
            new { boardUrl = "https://job-boards.greenhouse.io/gitlab/jobs/8860302002?redirect=http://169.254.169.254/" });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await ApiClient.TextAsync(res));
        var item = await ApiClient.JsonAsync(res);
        (item.GetProperty("name").GetString(), item.GetProperty("platform").GetString(), item.GetProperty("boardToken").GetString(),
            item.GetProperty("status").GetString(), item.GetProperty("openJobsCount").GetInt32(), item.GetProperty("origin").GetString())
            .Should().Be(("GitLab", "greenhouse", "gitlab", "resolved", 5, "user"));
        F.Http.Requests.Select(r => r.AbsoluteUri).Should().Equal([GitlabApi], "the user's URL is never fetched; only the fixed template");
        F.Jobs.History.Should().Contain(j => j.Type == typeof(Tailorly.App.Features.Sources.PollBoardJob), "watched boards are polled eagerly");

        var again = await u.Api.PostAsync("/api/v1/watched-companies", new { platform = "greenhouse", boardToken = "GitLab" });
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/watched-companies"));
        list.GetArrayLength().Should().Be(1);

        var id = item.GetProperty("id").GetGuid();
        (await u.Api.DeleteAsync($"/api/v1/watched-companies/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await u.Api.DeleteAsync($"/api/v1/watched-companies/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM company_ats_links WHERE board_token = 'gitlab'")).Should().Be(1,
            "removing from a watch list keeps the shared link");
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://localhost:5432/")]
    [InlineData("https://boards.greenhouse.io.evil.example/gitlab")]
    [InlineData("https://evil.example/?u=https://boards.greenhouse.io/gitlab")]
    [InlineData("https://user:pass@boards.greenhouse.io/gitlab")]
    [InlineData("https://boards.greenhouse.io:444/gitlab")]
    [InlineData("https://job-boards.eu.greenhouse.io/gitlab")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://boards.greenhouse.io/x")]
    [InlineData("https://boards.greenhouse.io/git%2Flab")]
    public async Task A_user_supplied_url_that_is_not_an_allowed_board_makes_no_outbound_call(string url)
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "ssrf");
        var res = await u.Api.PostAsync("/api/v1/watched-companies", new { boardUrl = url });
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.CodeAsync(res)).Should().Be("INVALID_BOARD_URL");
        F.Http.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("greenhouse", "../../admin")]
    [InlineData("greenhouse", "acme?x=1")]
    [InlineData("workday", "acme")]
    [InlineData(null, "acme")]
    public async Task An_invalid_platform_or_token_makes_no_outbound_call(string? platform, string token)
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "ssrf-token");
        var res = await u.Api.PostAsync("/api/v1/watched-companies", new { platform, boardToken = token });
        (await ApiClient.CodeAsync(res)).Should().Be("INVALID_BOARD_URL");
        F.Http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_or_empty_board_is_board_not_found()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "watch-404");
        F.Http.Always(GreenhouseApi + "nosuchboard/jobs", 404, Fixtures.Read("greenhouse/board_not_found.json"));
        F.Http.Always(GreenhouseApi + "emptyboard/jobs", 200, """{"jobs":[],"meta":{"total":0}}""");
        foreach (var token in new[] { "nosuchboard", "emptyboard" })
        {
            var res = await u.Api.PostAsync("/api/v1/watched-companies", new { boardUrl = $"https://boards.greenhouse.io/{token}" });
            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await ApiClient.CodeAsync(res)).Should().Be("BOARD_NOT_FOUND");
        }
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM user_watched_companies")).Should().Be(0);
    }

    [Fact]
    public async Task An_unreachable_board_is_503_not_board_not_found()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "watch-503");
        F.Http.Always(GreenhouseApi + "flaky/jobs", 503, "down");
        var res = await u.Api.PostAsync("/api/v1/watched-companies", new { boardUrl = "https://boards.greenhouse.io/flaky" });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task The_watch_list_holds_25_companies()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "watch-limit");
        for (var i = 0; i < 25; i++)
        {
            var linkId = await AddLinkAsync(F, "greenhouse", $"board-{i:D2}", $"Company {i}");
            await Db.ExecAsync(Cs, "INSERT INTO user_watched_companies (user_id, company_ats_link_id) VALUES (@u, @l)", ("u", u.Id), ("l", linkId));
        }
        F.Http.AlwaysFixture(GitlabApi, "greenhouse/gitlab_jobs.json");
        var res = await u.Api.PostAsync("/api/v1/watched-companies", new { boardUrl = "https://boards.greenhouse.io/gitlab" });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.CodeAsync(res)).Should().Be("WATCHLIST_LIMIT");
        F.Http.Requests.Should().BeEmpty("the limit is checked before the board");

        // Re-adding one already on the list is still fine at the limit.
        (await u.Api.PostAsync("/api/v1/watched-companies", new { platform = "greenhouse", boardToken = "board-03" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
