using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Ai;
using Tailorly.App.Features.Resumes;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.TailoringTestData;

namespace Tailorly.Tests.Integration.Tracker;

/// <summary>
/// Interview prep (PRD F10, ApiDesign §4.8): the async pattern on the single row per entry, talking points held to the
/// résumé's facts (one repair, then a clean failure), regeneration, the budget, the sweep, and the usage breakdown.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class InterviewPrepTests : IAsyncLifetime
{
    private readonly PostgresAppFixture _fx;
    private TailorlyAppFactory F => _fx.Factory;
    private string Cs => _fx.ConnectionString;

    public InterviewPrepTests(PostgresAppFixture fx) => _fx = fx;

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

    [Fact]
    public async Task Prep_is_generated_polled_regenerated_and_shown_on_the_entry()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "prep-flow");
        (await u.Api.GetAsync($"/api/v1/user-jobs/{uj}/interview-prep")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var first = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/interview-prep", new { });
        var second = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/interview-prep", new { });
        first.StatusCode.Should().Be(HttpStatusCode.Accepted, await ApiClient.TextAsync(first));
        second.StatusCode.Should().Be(HttpStatusCode.OK, "a run in flight is returned, not started twice");
        var queued = await ApiClient.JsonAsync(first);
        queued.GetProperty("status").GetString().Should().Be("queued");
        (await ApiClient.JsonAsync(second)).GetProperty("id").GetGuid().Should().Be(queued.GetProperty("id").GetGuid());

        MatchingTestData.Reply(F, AiTaskNames.InterviewPrep, Prep());
        await F.Jobs.RunAllAsync();
        var prep = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}/interview-prep"));
        prep.GetProperty("status").GetString().Should().Be("ready", prep.ToString());
        var content = prep.GetProperty("content");
        content.GetProperty("technicalTopics").GetArrayLength().Should().Be(2);
        content.GetProperty("behavioural")[0].GetProperty("talkingPointsFromResume")[0].GetString().Should().StartWith("Built REST APIs");
        content.GetProperty("gapsToPrepare").EnumerateArray().Select(x => x.GetString()).Should().Equal("Kubernetes");
        content.GetProperty("questionsToAsk").GetArrayLength().Should().Be(2);
        (await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}")))
            .GetProperty("documents").GetProperty("interviewPrepStatus").GetString().Should().Be("ready");

        // The prompt carries the job and the candidate's facts as delimited data only.
        var request = F.Ai.Requests.Last(r => r.Task == AiTaskNames.InterviewPrep);
        request.Messages[0].Text.Should().Contain("<job_data>");
        request.CacheablePrefix.Should().Contain("<candidate_data>").And.Contain("Built REST APIs");

        // Regenerating reuses the row (charged again).
        var again = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/interview-prep", new { });
        again.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var regenerating = await ApiClient.JsonAsync(again);
        regenerating.GetProperty("id").GetGuid().Should().Be(queued.GetProperty("id").GetGuid());
        regenerating.GetProperty("content").ValueKind.Should().Be(JsonValueKind.Null, "old notes are not shown as current while regenerating");
        MatchingTestData.Reply(F, AiTaskNames.InterviewPrep, Prep());
        await F.Jobs.RunAllAsync();
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM ai_usage WHERE user_id = @u AND task = 'interview_prep' AND bucket = 'documents'",
            ("u", u.Id))).Should().Be(2);
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM interview_preps WHERE user_job_id = @id", ("id", uj))).Should().Be(1);
    }

    [Fact]
    public async Task A_talking_point_with_an_invented_number_or_employer_is_repaired_once_then_fails_cleanly()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "prep-facts");
        var repaired = await PrepAsync(F, u, uj, Prep("Built REST APIs for 4,000 merchants using C# and PostgreSQL."), Prep());
        repaired.GetProperty("status").GetString().Should().Be("ready", repaired.ToString());
        var repairTurn = F.Ai.Requests.Last(r => r.Task == AiTaskNames.InterviewPrep);
        repairTurn.Messages.Should().HaveCountGreaterThan(1, "the second call is the repair turn");
        repairTurn.Messages.Last().Text.Should().Contain("talking_points_from_resume[0]").And.NotContain("4,000");

        var failed = await PrepAsync(F, u, uj, Prep("Led the platform team at Stripe."), Prep("Led the platform team at Stripe."));
        failed.GetProperty("status").GetString().Should().Be("failed");
        failed.GetProperty("errorCode").GetString().Should().Be("AI_INVALID_OUTPUT");
        failed.GetProperty("content").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Prep_needs_a_resume_a_description_and_Documents_budget_and_stuck_runs_time_out()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "prep-pre");
        await Db.SetUsedAsync(Cs, u.Id, "documents", 1_400_000, 1_400_000);
        var refused = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/interview-prep", new { });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.CodeAsync(refused)).Should().Be("BUDGET_EXCEEDED");
        await Db.SetUsedAsync(Cs, u.Id, "documents", 0, 1_400_000);

        var start = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/interview-prep", new { });
        var id = (await ApiClient.JsonAsync(start)).GetProperty("id").GetGuid();
        F.Jobs.Clear();
        await Db.ExecAsync(Cs, "UPDATE interview_preps SET updated_at = now() - interval '31 minutes' WHERE id = @id", ("id", id));
        await using (var scope = F.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<SweepStuckWorkJob>().RunAsync(TestContext.Current.CancellationToken);
        var prep = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}/interview-prep"));
        prep.GetProperty("status").GetString().Should().Be("failed");
        prep.GetProperty("errorCode").GetString().Should().Be("TIMEOUT");

        // Without an active résumé: RESUME_REQUIRED.
        var resumeId = (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/resumes"))).EnumerateArray()
            .Single(r => r.GetProperty("isActive").GetBoolean()).GetProperty("id").GetGuid();
        await Db.ExecAsync(Cs, "UPDATE resumes SET is_active = false WHERE user_id = @u", ("u", u.Id));
        var noResume = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/interview-prep", new { });
        noResume.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.CodeAsync(noResume)).Should().Be("RESUME_REQUIRED");
        await Db.ExecAsync(Cs, "UPDATE resumes SET is_active = true WHERE id = @id", ("id", resumeId));
    }

    [Fact]
    public async Task Usage_breaks_this_period_down_by_task_and_by_day()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "usage-breakdown");
        await PrepAsync(F, u, uj);
        var usage = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/usage"));

        var tasks = usage.GetProperty("byTask").EnumerateArray().ToDictionary(t => t.GetProperty("task").GetString()!);
        tasks.Should().ContainKeys("resume_parse", "job_fit_score", "interview_prep");
        tasks.Should().NotContainKey("job_enrichment", "shared enrichment is a platform cost");
        tasks["interview_prep"].GetProperty("bucket").GetString().Should().Be("documents");
        tasks["interview_prep"].GetProperty("calls").GetInt32().Should().Be(1);
        tasks["job_fit_score"].GetProperty("bucket").GetString().Should().Be("discovery");
        var documents = usage.GetProperty("buckets").EnumerateArray().Single(b => b.GetProperty("bucket").GetString() == "documents");
        tasks.Values.Where(t => t.GetProperty("bucket").GetString() == "documents").Sum(t => t.GetProperty("tokens").GetInt64())
            .Should().Be(documents.GetProperty("used").GetInt64(), "the breakdown adds up to the budget counter");

        var daily = usage.GetProperty("daily").EnumerateArray().ToList();
        daily.Should().HaveCount(30);
        daily[^1].GetProperty("tokens").GetInt64().Should().BeGreaterThan(0, "today's calls");
        daily.Select(d => d.GetProperty("date").GetString()).Should().BeInAscendingOrder();
    }
}
