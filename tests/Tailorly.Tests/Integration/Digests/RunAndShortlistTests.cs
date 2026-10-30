using System.Net;
using FluentAssertions;
using Tailorly.App.Ai;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.MatchingTestData;

namespace Tailorly.Tests.Integration.Digests;

/// <summary>
/// The per-user run through "Run now" (PRD F5–F7, ApiDesign §4.6): enrichment on demand and shared, scoring, the rules
/// (salary, minimum score, eligibility display, hard restrictions), the jobs API and the fallbacks.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RunAndShortlistTests : IAsyncLifetime
{
    private readonly PostgresAppFixture _fx;
    private TailorlyAppFactory F => _fx.Factory;
    private string Cs => _fx.ConnectionString;

    public RunAndShortlistTests(PostgresAppFixture fx) => _fx = fx;

    public ValueTask InitializeAsync()
    {
        F.Ai.ClearScript();
        F.Jobs.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>Scripted replies must not leak into the next test class of the shared factory.</summary>
    public ValueTask DisposeAsync()
    {
        F.Ai.ClearScript();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Run_now_enriches_scores_and_lists_the_job_with_attribution_and_an_explanation()
    {
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role);
        var jobId = await InsertJobAsync(F, $"Senior {role} Engineer", source: "remoteok");

        var digest = await RunNowAsync(F, u);
        digest.GetProperty("status").GetString().Should().Be("ready");
        digest.GetProperty("kind").GetString().Should().Be("manual");
        digest.GetProperty("degraded").GetBoolean().Should().BeFalse();
        digest.GetProperty("jobsScored").GetInt32().Should().Be(1);
        var item = Items(digest).Single();
        item.GetProperty("rank").GetInt32().Should().Be(1);
        item.GetProperty("job").GetProperty("id").GetGuid().Should().Be(jobId);

        var list = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/jobs"));
        list.GetProperty("total").GetInt32().Should().Be(1);
        var job = list.GetProperty("items")[0];
        job.GetProperty("eligibility").GetString().Should().Be("worldwide");
        job.GetProperty("salaryLabel").GetString().Should().Be("not_listed");
        job.GetProperty("score").GetProperty("fitScore").GetInt32().Should().Be(9);
        job.GetProperty("score").GetProperty("reasons").GetArrayLength().Should().BeInRange(1, 3);
        var source = job.GetProperty("source");
        source.GetProperty("key").GetString().Should().Be("remoteok");
        source.GetProperty("name").GetString().Should().Be("Remote OK");
        source.GetProperty("attributionText").GetString().Should().Be("Source: Remote OK");
        source.GetProperty("sourceUrl").GetString().Should().StartWith("https://remoteok.com/remote-jobs/");
        job.GetProperty("applyUrl").GetString().Should().StartWith("https://");

        var detail = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/jobs/{jobId}"));
        detail.GetProperty("description").GetString().Should().Contain("payment infrastructure");
        detail.GetProperty("summary").GetString().Should().NotBeNullOrEmpty();
        detail.GetProperty("skillsRequired").EnumerateArray().Select(s => s.GetString()).Should().Contain("C#");
    }

    [Fact]
    public async Task Enrichment_is_shared_and_a_job_is_never_scored_twice_for_the_same_resume_version()
    {
        var role = NewRole();
        var a = await CreateMatcherAsync(F, role, "share-a");
        var b = await CreateMatcherAsync(F, role, "share-b");
        var jobId = await InsertJobAsync(F, $"{role} Developer");
        int Calls(string task) => F.Ai.Requests.Count(r => r.Task == task);

        var enrich0 = Calls(AiTaskNames.JobEnrichment);
        var score0 = Calls(AiTaskNames.JobFitScore);
        await RunNowAsync(F, a);
        Calls(AiTaskNames.JobEnrichment).Should().Be(enrich0 + 1);
        Calls(AiTaskNames.JobFitScore).Should().Be(score0 + 1);

        await RunNowAsync(F, b); // same job: enrichment reused, scored for b
        Calls(AiTaskNames.JobEnrichment).Should().Be(enrich0 + 1);
        Calls(AiTaskNames.JobFitScore).Should().Be(score0 + 2);

        await RunNowAsync(F, a); // already scored for a's résumé version
        Calls(AiTaskNames.JobFitScore).Should().Be(score0 + 2);
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM job_enrichment WHERE job_id = @j", ("j", jobId))).Should().Be(1);
        (await Db.ScalarAsync<string>(Cs, "SELECT data->>'eligibility_source' FROM job_enrichment WHERE job_id = @j", ("j", jobId)))
            .Should().Be("model");
    }

    [Fact]
    public async Task Enrichment_is_not_charged_to_the_user_but_scoring_is_charged_to_discovery()
    {
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role);
        await InsertJobAsync(F, $"{role} Engineer");
        await RunNowAsync(F, u);

        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM ai_usage WHERE task = 'job_enrichment' AND user_id = @u", ("u", u.Id)))
            .Should().Be(0);
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM ai_usage WHERE task = 'job_fit_score' AND user_id = @u AND bucket = 'discovery'",
            ("u", u.Id))).Should().Be(1);
        var usage = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/usage"));
        usage.GetProperty("buckets").EnumerateArray().Single(x => x.GetProperty("bucket").GetString() == "discovery")
            .GetProperty("used").GetInt64().Should().BePositive();
    }

    [Fact]
    public async Task A_hard_restriction_beats_the_model_and_the_job_is_never_shown()
    {
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role);
        var restricted = await InsertJobAsync(F, $"{role} Engineer (Payments)",
            description: Description + " Candidates must be authorized to work in the United States.");
        var open = await InsertJobAsync(F, $"{role} Engineer (Platform)");
        ReplyByTitle(F, AiTaskNames.JobEnrichment, 2, ("(Payments)", Enrichment("worldwide", "We hire from anywhere in the world")));

        var digest = await RunNowAsync(F, u);
        Items(digest).Select(i => i.GetProperty("job").GetProperty("id").GetGuid()).Should().Equal(open);
        (await Db.ScalarAsync<string>(Cs, "SELECT eligibility FROM job_enrichment WHERE job_id = @j", ("j", restricted))).Should().Be("restricted");
        (await Db.ScalarAsync<string>(Cs, "SELECT data->>'eligibility_source' FROM job_enrichment WHERE job_id = @j", ("j", restricted)))
            .Should().Be("rules");
        (await u.Api.GetAsync($"/api/v1/jobs/{restricted}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Jobs_below_the_minimum_score_are_hidden_until_the_filter_is_lowered()
    {
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role);
        var high = await InsertJobAsync(F, $"{role} Engineer High");
        // A listed salary: a job without one needs the user's minimum score anyway (salary rule, PRD §7.5).
        var low = await InsertJobAsync(F, $"{role} Engineer Low", salaryMin: 40_000, salaryMax: 50_000, currency: "USD", salaryKind: "stated");
        ReplyByTitle(F, AiTaskNames.JobFitScore, 2, ("Engineer High", Fit(9)), ("Engineer Low", Fit(6)));

        var digest = await RunNowAsync(F, u);
        Items(digest).Select(i => i.GetProperty("job").GetProperty("id").GetGuid()).Should().Equal(high);
        JobIds(await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/jobs"))).Should().Equal(high);
        JobIds(await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/jobs?minScore=6"))).Should().Equal(high, low);
        (await u.Api.GetAsync($"/api/v1/jobs/{low}")).StatusCode.Should().Be(HttpStatusCode.OK, "scored jobs stay readable");
        (await u.Api.GetAsync("/api/v1/jobs?minScore=11")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task The_salary_rule_is_applied_with_currency_conversion()
    {
        var role = NewRole();
        // 30L–55L INR; static rate 95 INR/USD.
        var u = await CreateMatcherAsync(F, role, prefs: p =>
        {
            p["salaryMin"] = 3_000_000;
            p["salaryMax"] = 5_500_000;
            p["salaryCurrency"] = "INR";
        });
        var inside = await InsertJobAsync(F, $"{role} Engineer A", salaryMin: 40_000, salaryMax: 50_000, currency: "USD", salaryKind: "stated");
        var below = await InsertJobAsync(F, $"{role} Engineer B", salaryMin: 10_000, salaryMax: 20_000, currency: "USD", salaryKind: "stated");
        var estimate = await InsertJobAsync(F, $"{role} Engineer C", salaryMin: 10_000, salaryMax: 20_000, currency: "USD", salaryKind: "estimated");
        var unknown = await InsertJobAsync(F, $"{role} Engineer D");

        var digest = await RunNowAsync(F, u);
        Items(digest).Select(i => i.GetProperty("job").GetProperty("id").GetGuid()).Should().BeEquivalentTo([inside, estimate, unknown]);

        var page = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/jobs"));
        var a = page.GetProperty("items").EnumerateArray().Single(j => j.GetProperty("id").GetGuid() == inside);
        a.GetProperty("salaryLabel").GetString().Should().Be("stated");
        a.GetProperty("salary").GetProperty("approxInr").GetProperty("min").GetInt64().Should().Be(3_800_000);
        page.GetProperty("items").EnumerateArray().Single(j => j.GetProperty("id").GetGuid() == estimate)
            .GetProperty("salaryLabel").GetString().Should().Be("estimated");
        JobIds(await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/jobs?salary=not_listed"))).Should().Equal(unknown);
        JobIds(page).Should().NotContain(below);

        // Turning off "include unspecified salary" hides the not-listed job from the list too.
        await SetPreferencesAsync(u, p => p["includeUnspecifiedSalary"] = false);
        JobIds(await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/jobs"))).Should().NotContain(unknown);
    }

    [Fact]
    public async Task Unclear_eligibility_is_shown_in_the_app_with_its_label()
    {
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role);
        var unclear = await InsertJobAsync(F, $"{role} Engineer", description: Description.Replace("We hire from anywhere in the world and", "We"));
        Reply(F, AiTaskNames.JobEnrichment, Enrichment("unclear"));

        await RunNowAsync(F, u);
        var page = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/jobs?eligibility=unclear"));
        JobIds(page).Should().Equal(unclear);
        page.GetProperty("items")[0].GetProperty("eligibility").GetString().Should().Be("unclear");
        JobIds(await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/jobs?eligibility=worldwide"))).Should().BeEmpty();
    }

    [Fact]
    public async Task Preferences_filter_out_of_scope_and_excluded_jobs_before_any_model_call()
    {
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role, prefs: p =>
        {
            p["excludedCompanies"] = new[] { "Blocked Co" };
            p["excludedKeywords"] = new[] { "unpaid" };
            p["searchScope"] = "global_remote_only";
        });
        await InsertJobAsync(F, $"{role} Engineer", company: "Blocked Co Inc.");
        await InsertJobAsync(F, $"{role} Engineer (unpaid)");
        await InsertJobAsync(F, $"{role} Engineer Onsite", location: "Pune, India", workMode: "onsite");
        await InsertJobAsync(F, $"{role} Engineer US", location: "Remote - US only");
        var calls = F.Ai.Requests.Count;

        var digest = await RunNowAsync(F, u);
        Items(digest).Should().BeEmpty();
        F.Ai.Requests.Count.Should().Be(calls, "nothing passed the raw prefilter, so no model was called");
    }

    [Fact]
    public async Task Disabled_sources_closed_and_duplicate_listings_are_not_candidates()
    {
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role);
        await Db.ExecAsync(Cs, "UPDATE job_sources SET enabled = false WHERE key = 'himalayas'"); // as seeded
        await InsertJobAsync(F, $"{role} Engineer H", source: "himalayas");
        var closed = await InsertJobAsync(F, $"{role} Engineer C");
        await Db.ExecAsync(Cs, "UPDATE jobs SET closed_at = now() WHERE id = @j", ("j", closed));
        var old = await InsertJobAsync(F, $"{role} Engineer O", createdAt: DateTimeOffset.UtcNow.AddDays(-4));

        var digest = await RunNowAsync(F, u);
        Items(digest).Should().BeEmpty();
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM user_job_scores WHERE user_id = @u", ("u", u.Id)))
            .Should().Be(0, "a disabled source, a closed listing and one older than the 72-hour lookback ({0}) are not candidates", old);
    }

    [Fact]
    public async Task A_job_not_scored_for_the_caller_is_not_visible()
    {
        var role = NewRole();
        var owner = await CreateMatcherAsync(F, role, "visible-a");
        var other = await TestUsers.CreateVerifiedAsync(F, "visible-b");
        var jobId = await InsertJobAsync(F, $"{role} Engineer");
        await RunNowAsync(F, owner);

        (await owner.Api.GetAsync($"/api/v1/jobs/{jobId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await other.Api.GetAsync($"/api/v1/jobs/{jobId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.Api.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        JobIds(await ApiClient.JsonAsync(await other.Api.GetAsync("/api/v1/jobs?minScore=1"))).Should().NotContain(jobId);
    }

    [Fact]
    public async Task Run_now_needs_a_resume_returns_the_run_in_flight_and_allows_three_a_day()
    {
        var noResume = await TestUsers.CreateVerifiedAsync(F, "run-noresume");
        var r = await noResume.Api.PostAsync("/api/v1/runs");
        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.JsonAsync(r)).GetProperty("code").GetString().Should().Be("RESUME_REQUIRED");

        var u = await CreateMatcherAsync(F, NewRole());
        var first = await u.Api.PostAsync("/api/v1/runs");
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        first.Headers.Location!.ToString().Should().StartWith("/api/v1/digests/");
        var again = await u.Api.PostAsync("/api/v1/runs");
        again.StatusCode.Should().Be(HttpStatusCode.OK, "a run in flight is returned, not duplicated");
        (await ApiClient.JsonAsync(again)).GetProperty("id").GetGuid().Should().Be((await ApiClient.JsonAsync(first)).GetProperty("id").GetGuid());
        await F.Jobs.RunAllAsync();

        await RunNowAsync(F, u);
        await RunNowAsync(F, u);
        var fourth = await u.Api.PostAsync("/api/v1/runs");
        fourth.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ApiClient.JsonAsync(fourth)).GetProperty("code").GetString().Should().Be("RUN_LIMIT_REACHED");
        fourth.Headers.RetryAfter.Should().NotBeNull();
        F.Emails.To(u.Email).Should().NotContain(m => m.Subject.Contains("match", StringComparison.Ordinal), "manual runs never email");

        var history = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/digests?kind=manual"));
        history.GetProperty("total").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Discovery_budget_used_up_gives_a_rule_based_degraded_digest()
    {
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role);
        var jobId = await InsertJobAsync(F, $"{role} Engineer");
        await Db.ExecAsync(Cs, """
            INSERT INTO user_budget_counters (user_id, period_key, bucket, limit_tokens, used_tokens)
            VALUES (@u, to_char(now() AT TIME ZONE 'Asia/Kolkata', 'YYYY-MM'), 'discovery', 600000, 600000)
            ON CONFLICT (user_id, period_key, bucket) DO UPDATE SET used_tokens = 600000
            """, ("u", u.Id));
        var scoring = F.Ai.Requests.Count(r => r.Task == AiTaskNames.JobFitScore);

        var digest = await RunNowAsync(F, u);
        digest.GetProperty("status").GetString().Should().Be("ready");
        digest.GetProperty("degraded").GetBoolean().Should().BeTrue();
        var item = Items(digest).Single().GetProperty("job");
        item.GetProperty("id").GetGuid().Should().Be(jobId);
        item.GetProperty("score").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null, "rule-ranked jobs have no fit score");
        F.Ai.Requests.Count(r => r.Task == AiTaskNames.JobFitScore).Should().Be(scoring, "no scoring call when the budget is used up");
        (await Db.ScalarAsync<string>(Cs, "SELECT model FROM user_job_scores WHERE user_id = @u", ("u", u.Id))).Should().Be("rules");
    }

    [Fact]
    public async Task The_platform_ceiling_stops_enrichment_and_jobs_are_ranked_by_rules()
    {
        await using var f = new TailorlyAppFactory(Cs, new Dictionary<string, string?> { ["Ai:PlatformDailyEnrichmentTokenCeiling"] = "1" });
        // Any platform enrichment today already exceeds a ceiling of 1 token; make sure there is some.
        await Db.ExecAsync(Cs, """
            INSERT INTO ai_usage (user_id, task, model, bucket, period_key, counted_tokens, success)
            VALUES (NULL, 'job_enrichment', 'test-model', 'platform', 'test', 10, true)
            """);
        var role = NewRole();
        var u = await CreateMatcherAsync(f, role);
        var jobId = await InsertJobAsync(f, $"{role} Engineer");

        var digest = await RunNowAsync(f, u);
        digest.GetProperty("degraded").GetBoolean().Should().BeTrue();
        Items(digest).Single().GetProperty("job").GetProperty("id").GetGuid().Should().Be(jobId);
        f.Ai.Requests.Should().NotContain(r => r.Task == AiTaskNames.JobEnrichment);
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM job_enrichment WHERE job_id = @j", ("j", jobId))).Should().Be(0);
    }

    [Fact]
    public async Task A_manual_run_falls_back_to_rules_at_once_when_the_AI_is_unavailable()
    {
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role);
        var jobId = await InsertJobAsync(F, $"{role} Engineer");
        FailNext(F, AiTaskNames.JobEnrichment);

        var digest = await RunNowAsync(F, u);
        digest.GetProperty("status").GetString().Should().Be("ready");
        digest.GetProperty("degraded").GetBoolean().Should().BeTrue();
        Items(digest).Single().GetProperty("job").GetProperty("id").GetGuid().Should().Be(jobId);
    }

    [Fact]
    public async Task An_invalid_reply_skips_only_that_job()
    {
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role);
        var good = await InsertJobAsync(F, $"{role} Engineer Good");
        await InsertJobAsync(F, $"{role} Engineer Broken");
        ReplyByTitle(F, AiTaskNames.JobEnrichment, 4, ("Engineer Broken", new { nonsense = true }));

        var digest = await RunNowAsync(F, u);
        digest.GetProperty("degraded").GetBoolean().Should().BeFalse();
        Items(digest).Select(i => i.GetProperty("job").GetProperty("id").GetGuid()).Should().Equal(good);
    }

    [Fact]
    public async Task Jobs_list_validates_and_pages()
    {
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role);
        for (var i = 0; i < 3; i++) await InsertJobAsync(F, $"{role} Engineer {i}");
        await RunNowAsync(F, u);

        var page = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/jobs?pageSize=2&page=2&sort=-posted"));
        page.GetProperty("total").GetInt32().Should().Be(3);
        page.GetProperty("items").GetArrayLength().Should().Be(1);
        page.GetProperty("pageSize").GetInt32().Should().Be(2);
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/jobs?pageSize=500"))).GetProperty("pageSize").GetInt32().Should().Be(50);
        JobIds(await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/jobs?q={role}%20Engineer%201"))).Should().HaveCount(1);
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/jobs?savedOnly=true"))).GetProperty("total").GetInt32().Should().Be(0);
        foreach (var bad in new[] { "eligibility=nope", "workMode=space", "salary=lots", "sort=score", "minScore=0" })
            (await u.Api.GetAsync("/api/v1/jobs?" + bad)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, bad);
        (await u.Api.GetAsync($"/api/v1/jobs?digestId={Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
