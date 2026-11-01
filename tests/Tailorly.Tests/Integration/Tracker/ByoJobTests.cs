using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Ai;
using Tailorly.App.Features.Resumes;
using Tailorly.App.Infrastructure.Ai;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.TailoringTestData;

namespace Tailorly.Tests.Integration.Tracker;

/// <summary>
/// Bring-your-own job (PRD F12, ApiDesign §4.6, ADR-008): pasted text becomes a tracker entry that is enriched on the
/// user's Documents budget, scored on Discovery, and then analysed and tailored like any other job. The link is stored
/// as text and never fetched.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ByoJobTests : IAsyncLifetime
{
    private const string Link = "https://www.linkedin.com/jobs/view/4242424242/";
    private readonly PostgresAppFixture _fx;
    private TailorlyAppFactory F => _fx.Factory;
    private string Cs => _fx.ConnectionString;

    public ByoJobTests(PostgresAppFixture fx) => _fx = fx;

    public ValueTask InitializeAsync()
    {
        F.Ai.ClearScript();
        F.Jobs.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        F.Ai.ClearScript();
        return ValueTask.CompletedTask;
    }

    private static object Fit(int score = 8) => new
    {
        fit_score = score,
        reasons = new[] { "Strong C# and PostgreSQL match", "Payments domain fits" },
        matched_skills = new[] { "C#", "PostgreSQL" },
        missing_skills = new[] { "Kubernetes" },
        confidence = "high",
    };

    private async Task<TestUser> UserWithResumeAsync(string tag)
    {
        var u = await TestUsers.CreateVerifiedAsync(F, tag);
        MatchingTestData.Reply(F, AiTaskNames.ResumeParse, SampleProfile());
        await TestUsers.UploadParsedResumeAsync(F, u);
        return u;
    }

    [Fact]
    public async Task A_pasted_job_is_enriched_scored_and_tailored_without_fetching_its_link()
    {
        var u = await UserWithResumeAsync("byo-flow");
        var requestsBefore = F.Http.Requests.Count;
        var enrichmentsBefore = F.Ai.Requests.Count(r => r.Task == AiTaskNames.ByoJobEnrichment);
        MatchingTestData.Reply(F, AiTaskNames.ByoJobEnrichment,
            MatchingTestData.Enrichment(eligibility: "india_eligible", evidence: "we hire across APAC time zones"));
        MatchingTestData.Reply(F, AiTaskNames.JobFitScore, Fit());

        var res = await u.Api.PostAsync("/api/v1/jobs/byo",
            new { description = ByoDescription, url = Link, title = "Platform Engineer", companyName = "Brightwave" });
        res.StatusCode.Should().Be(HttpStatusCode.Accepted, await ApiClient.TextAsync(res));
        res.Headers.Location!.ToString().Should().StartWith("/api/v1/user-jobs/");
        var entry = await ApiClient.JsonAsync(res);
        var uj = entry.GetProperty("id").GetGuid();
        entry.GetProperty("isByo").GetBoolean().Should().BeTrue();
        entry.GetProperty("jobId").ValueKind.Should().Be(JsonValueKind.Null);
        entry.GetProperty("status").GetString().Should().Be("saved");
        entry.GetProperty("enrichmentStatus").GetString().Should().Be("queued");
        entry.GetProperty("applyUrl").GetString().Should().Be(Link);
        entry.GetProperty("source").GetProperty("key").GetString().Should().Be("byo");
        entry.GetProperty("source").GetProperty("name").GetString().Should().Be("Added by you");

        await F.Jobs.RunAllAsync();
        entry = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}"));
        entry.GetProperty("enrichmentStatus").GetString().Should().Be("ready", entry.ToString());
        entry.GetProperty("fitScore").GetInt32().Should().Be(8);
        var assessment = entry.GetProperty("assessment");
        assessment.GetProperty("eligibility").GetString().Should().NotBeNullOrEmpty();
        assessment.GetProperty("fitReasons").EnumerateArray().Select(x => x.GetString()).Should().Contain("Strong C# and PostgreSQL match");
        assessment.GetProperty("missingSkills").EnumerateArray().Select(x => x.GetString()).Should().Equal("Kubernetes");

        // Never fetched: no outbound request at all, and the link never reached the model either.
        F.Http.Requests.Count.Should().Be(requestsBefore);
        F.Http.Requests.Should().NotContain(r => r.Host.Contains("linkedin", StringComparison.OrdinalIgnoreCase));
        F.Ai.Requests.Should().NotContain(r => r.Messages.Any(m => m.Text.Contains("linkedin", StringComparison.OrdinalIgnoreCase)));

        (await Db.StringsAsync(Cs, "SELECT task || ':' || bucket FROM ai_usage WHERE user_id = @u AND task <> 'resume_parse' ORDER BY id", ("u", u.Id)))
            .Should().Equal("byo_job_enrichment:documents", "job_fit_score:discovery");

        // The same flow as a listing: analysis, tailoring, cover letter, interview prep.
        var analysis = await AnalyseAsync(F, u, uj);
        analysis.GetProperty("status").GetString().Should().Be("ready", analysis.ToString());
        var doc = await TailorAsync(F, u, uj, Tailor(Edit("Built REST", "Built REST APIs in C# and PostgreSQL for the payments team.")));
        doc.GetProperty("status").GetString().Should().Be("proposed");
        (await PrepAsync(F, u, uj)).GetProperty("status").GetString().Should().Be("ready");
        F.Ai.Requests.Count(r => r.Task == AiTaskNames.ByoJobEnrichment).Should().Be(enrichmentsBefore + 1, "the stored enrichment is reused");
        F.Http.Requests.Count.Should().Be(requestsBefore);
    }

    [Fact]
    public async Task Without_a_ready_resume_or_Discovery_budget_the_job_is_ready_without_a_score()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "byo-noresume");
        var res = await u.Api.PostAsync("/api/v1/jobs/byo", new { description = ByoDescription });
        res.StatusCode.Should().Be(HttpStatusCode.Accepted, await ApiClient.TextAsync(res));
        var uj = (await ApiClient.JsonAsync(res)).GetProperty("id").GetGuid();
        await F.Jobs.RunAllAsync();
        var entry = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}"));
        entry.GetProperty("enrichmentStatus").GetString().Should().Be("ready");
        entry.GetProperty("fitScore").ValueKind.Should().Be(JsonValueKind.Null);
        entry.GetProperty("title").GetString().Should().Be("Untitled role");
        entry.GetProperty("companyName").GetString().Should().Be("Company not given");
        entry.GetProperty("assessment").GetProperty("fitReasons").GetArrayLength().Should().Be(0);

        var v = await UserWithResumeAsync("byo-nodiscovery");
        await Db.SetUsedAsync(Cs, v.Id, "discovery", 600_000, 600_000);
        var res2 = await v.Api.PostAsync("/api/v1/jobs/byo", new { description = ByoDescription });
        var uj2 = (await ApiClient.JsonAsync(res2)).GetProperty("id").GetGuid();
        await F.Jobs.RunAllAsync();
        var entry2 = await ApiClient.JsonAsync(await v.Api.GetAsync($"/api/v1/user-jobs/{uj2}"));
        entry2.GetProperty("enrichmentStatus").GetString().Should().Be("ready");
        entry2.GetProperty("fitScore").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_failed_enrichment_is_shown_and_retried_by_pasting_again()
    {
        var u = await UserWithResumeAsync("byo-fail");
        F.Ai.Enqueue(AiTaskNames.ByoJobEnrichment, "not json");
        F.Ai.Enqueue(AiTaskNames.ByoJobEnrichment, "still not json");
        var uj = (await ApiClient.JsonAsync(await u.Api.PostAsync("/api/v1/jobs/byo", new { description = ByoDescription }))).GetProperty("id").GetGuid();
        await F.Jobs.RunAllAsync();
        var entry = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}"));
        entry.GetProperty("enrichmentStatus").GetString().Should().Be("failed");
        entry.GetProperty("enrichmentErrorCode").GetString().Should().Be("AI_INVALID_OUTPUT");
        entry.GetProperty("assessment").ValueKind.Should().Be(JsonValueKind.Null);

        var retried = await u.Api.PutAsync($"/api/v1/user-jobs/{uj}/description", new { description = ByoDescription });
        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiClient.JsonAsync(retried)).GetProperty("enrichmentStatus").GetString().Should().Be("queued");
        await F.Jobs.RunAllAsync();
        entry = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}"));
        entry.GetProperty("enrichmentStatus").GetString().Should().Be("ready");
        entry.GetProperty("enrichmentErrorCode").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Input_is_validated_and_the_Documents_budget_is_checked_first()
    {
        var u = await UserWithResumeAsync("byo-validate");
        foreach (var bad in new object[]
                 {
                     new { description = "" }, new { description = new string('x', 30_001) },
                     new { description = ByoDescription, url = "ftp://example.test/job" },
                     new { description = ByoDescription, url = "file:///etc/passwd" },
                     new { description = ByoDescription, url = "linkedin.com/jobs/1" },
                     new { description = ByoDescription, title = new string('t', 201) },
                 })
        {
            var res = await u.Api.PostAsync("/api/v1/jobs/byo", bad);
            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, JsonSerializer.Serialize(bad).Length.ToString());
        }

        await Db.SetUsedAsync(Cs, u.Id, "documents", 1_400_000, 1_400_000);
        var refused = await u.Api.PostAsync("/api/v1/jobs/byo", new { description = ByoDescription });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.CodeAsync(refused)).Should().Be("BUDGET_EXCEEDED");
        await Db.SetUsedAsync(Cs, u.Id, "documents", 0, 1_400_000);
        F.Jobs.PendingCount.Should().Be(0, "nothing was queued");
    }

    [Fact]
    public async Task A_stuck_enrichment_is_failed_with_TIMEOUT()
    {
        var u = await UserWithResumeAsync("byo-sweep");
        var uj = (await ApiClient.JsonAsync(await u.Api.PostAsync("/api/v1/jobs/byo", new { description = ByoDescription }))).GetProperty("id").GetGuid();
        F.Jobs.Clear();
        await Db.ExecAsync(Cs, "UPDATE user_jobs SET updated_at = now() - interval '31 minutes' WHERE id = @id", ("id", uj));
        await using (var scope = F.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<SweepStuckWorkJob>().RunAsync(TestContext.Current.CancellationToken);
        var entry = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}"));
        entry.GetProperty("enrichmentStatus").GetString().Should().Be("failed");
        entry.GetProperty("enrichmentErrorCode").GetString().Should().Be("TIMEOUT");
    }
}
