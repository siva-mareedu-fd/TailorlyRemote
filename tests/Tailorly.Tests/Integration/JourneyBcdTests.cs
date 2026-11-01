using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Tailorly.App.Ai;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.TailoringTestData;

namespace Tailorly.Tests.Integration;

/// <summary>
/// PRD §3 Journeys B, C and D end to end through the API (M5 exit criterion), with the fake model provider:
/// <list type="bullet">
/// <item>B: the scheduled digest email → the job → Tailor (analysis, diff, decisions, ATS before → after) → the .docx and a
/// cover letter → Apply (the source's own link) → Applied.</item>
/// <item>C: a job pasted from LinkedIn → the same score, analysis and tailoring → tracked.</item>
/// <item>D: an entry moved to Interviewing → interview prep.</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class JourneyBcdTests : IAsyncLifetime
{
    private readonly PostgresAppFixture _fx;
    private TailorlyAppFactory F => _fx.Factory;

    public JourneyBcdTests(PostgresAppFixture fx) => _fx = fx;

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

    private static object HighFit() => new
    {
        fit_score = 9,
        reasons = new[] { "Your .NET and PostgreSQL work matches the core stack" },
        matched_skills = new[] { "C#", "PostgreSQL" },
        missing_skills = new[] { "Kubernetes" },
        confidence = "high",
    };

    [Fact]
    public async Task Journey_B_digest_email_to_tailored_documents_and_applied()
    {
        // Ravi's schedule is due now; the shortlist's one job is scored and emailed.
        var india = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var due = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, india).AddMinutes(-1).ToString("HH:mm", CultureInfo.InvariantCulture);
        var role = MatchingTestData.NewRole();
        var u = await MatchingTestData.CreateMatcherAsync(F, role, "journey-b", p =>
        {
            p["scheduleEnabled"] = true;
            p["scheduleLocalTime"] = due;
        });
        MatchingTestData.Reply(F, AiTaskNames.ResumeParse, SampleProfile());
        await TestUsers.UploadParsedResumeAsync(F, u);
        var jobId = await MatchingTestData.InsertJobAsync(F, $"Senior {role} Engineer", company: "Acme Cloud");
        MatchingTestData.Reply(F, AiTaskNames.JobEnrichment, MatchingTestData.Enrichment());
        MatchingTestData.Reply(F, AiTaskNames.JobFitScore, HighFit());
        await MatchingTestData.DispatchAsync(F, DateTimeOffset.UtcNow);
        await F.Jobs.RunAllAsync(job => job.Args.Contains(u.Id));

        var mail = F.Emails.To(u.Email).Where(m => m.ListUnsubscribeUrl is not null).Should().ContainSingle().Subject;
        WebUtility.HtmlDecode(mail.HtmlBody).Should().Contain($"/shortlist?job={jobId}");

        // Opens the job: score, eligibility and salary label.
        var job = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/jobs/{jobId}"));
        job.GetProperty("score").GetProperty("fitScore").GetInt32().Should().Be(9);
        job.GetProperty("eligibility").GetString().Should().NotBeNullOrEmpty();
        job.GetProperty("salaryLabel").GetString().Should().NotBeNullOrEmpty();
        var applyUrl = job.GetProperty("applyUrl").GetString();

        // Tailor: track it, analyse, review the diff, finalise; ATS before → after.
        var tracked = await u.Api.PostAsync("/api/v1/user-jobs", new { jobId });
        tracked.StatusCode.Should().Be(HttpStatusCode.Created, await ApiClient.TextAsync(tracked));
        var uj = (await ApiClient.JsonAsync(tracked)).GetProperty("id").GetGuid();
        var analysis = await AnalyseAsync(F, u, uj);
        var before = analysis.GetProperty("atsScore").GetInt32();
        var doc = await TailorAsync(F, u, uj, Tailor(Edit("Built REST", "Built REST APIs in C# and PostgreSQL for the payments team.")));
        var docId = doc.GetProperty("id").GetGuid();
        Edits(doc).Single().GetProperty("diff").EnumerateArray().Should().Contain(d => d.GetProperty("type").GetString() == "insert");
        (await DecideAsync(u, docId, (Edits(doc).Single().GetProperty("id").GetString()!, "accepted"))).StatusCode.Should().Be(HttpStatusCode.OK);
        MatchingTestData.Reply(F, AiTaskNames.AtsAnalysis, Analysis());
        (await u.Api.PostAsync($"/api/v1/tailored-documents/{docId}/finalize")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await F.Jobs.RunAllAsync();
        doc = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/tailored-documents/{docId}"));
        doc.GetProperty("status").GetString().Should().Be("ready", doc.ToString());
        doc.GetProperty("atsBefore").GetInt32().Should().Be(before);
        doc.GetProperty("atsAfter").GetInt32().Should().BeGreaterThanOrEqualTo(before);

        // Downloads the .docx and a cover letter.
        var docx = await u.Api.GetAsync($"/api/v1/tailored-documents/{docId}/download");
        docx.StatusCode.Should().Be(HttpStatusCode.OK);
        docx.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        MatchingTestData.Reply(F, AiTaskNames.CoverLetter, Letter());
        var letter = await ApiClient.JsonAsync(await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { tone = "concise" }));
        await F.Jobs.RunAllAsync();
        (await u.Api.GetAsync($"/api/v1/tailored-documents/{letter.GetProperty("id").GetGuid()}/download")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Apply opens the source's link (kept on the card); "Did you apply?" → Applied.
        var entry = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}"));
        entry.GetProperty("status").GetString().Should().Be("tailored");
        entry.GetProperty("applyUrl").GetString().Should().Be(applyUrl);
        entry.GetProperty("atsBefore").GetInt32().Should().Be(before);
        entry.GetProperty("atsAfter").GetInt32().Should().Be(doc.GetProperty("atsAfter").GetInt32());
        var applied = await u.Api.PatchAsync($"/api/v1/user-jobs/{uj}",
            new { status = "applied", concurrencyToken = entry.GetProperty("concurrencyToken").GetString() });
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(applied));
        (await ApiClient.JsonAsync(applied)).GetProperty("statusHistory").EnumerateArray().Select(h => h.GetProperty("status").GetString())
            .Should().Equal("saved", "tailored", "applied");
        var board = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/user-jobs?status=applied"));
        board.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(uj);
    }

    [Fact]
    public async Task Journey_C_a_pasted_job_gets_the_same_scoring_and_tailoring()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "journey-c");
        MatchingTestData.Reply(F, AiTaskNames.ResumeParse, SampleProfile());
        await TestUsers.UploadParsedResumeAsync(F, u);
        var outbound = F.Http.Requests.Count;

        MatchingTestData.Reply(F, AiTaskNames.ByoJobEnrichment, MatchingTestData.Enrichment());
        MatchingTestData.Reply(F, AiTaskNames.JobFitScore, HighFit());
        var added = await u.Api.PostAsync("/api/v1/jobs/byo", new
        {
            description = ByoDescription, url = "https://www.linkedin.com/jobs/view/1234567890/", title = "Platform Engineer", companyName = "Brightwave",
        });
        added.StatusCode.Should().Be(HttpStatusCode.Accepted, await ApiClient.TextAsync(added));
        var uj = (await ApiClient.JsonAsync(added)).GetProperty("id").GetGuid();
        await F.Jobs.RunAllAsync();
        var entry = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}"));
        entry.GetProperty("fitScore").GetInt32().Should().Be(9);
        entry.GetProperty("assessment").GetProperty("fitReasons").GetArrayLength().Should().Be(1);

        var analysis = await AnalyseAsync(F, u, uj);
        analysis.GetProperty("status").GetString().Should().Be("ready");
        var doc = await TailorAsync(F, u, uj, Tailor(Edit("Built REST", "Built REST APIs in C# and PostgreSQL for the payments team.")));
        await DecideAsync(u, doc.GetProperty("id").GetGuid(), (Edits(doc).Single().GetProperty("id").GetString()!, "accepted"));
        MatchingTestData.Reply(F, AiTaskNames.AtsAnalysis, Analysis());
        await u.Api.PostAsync($"/api/v1/tailored-documents/{doc.GetProperty("id").GetGuid()}/finalize");
        await F.Jobs.RunAllAsync();
        (await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/tailored-documents/{doc.GetProperty("id").GetGuid()}")))
            .GetProperty("status").GetString().Should().Be("ready");

        var list = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/user-jobs"));
        var card = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == uj);
        card.GetProperty("status").GetString().Should().Be("tailored");
        card.GetProperty("isByo").GetBoolean().Should().BeTrue();
        F.Http.Requests.Count.Should().Be(outbound, "the pasted link is never fetched");
    }

    [Fact]
    public async Task Journey_D_an_interviewing_job_gets_interview_prep()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "journey-d");
        var entry = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}"));
        var moved = await u.Api.PatchAsync($"/api/v1/user-jobs/{uj}",
            new { status = "interviewing", concurrencyToken = entry.GetProperty("concurrencyToken").GetString() });
        moved.StatusCode.Should().Be(HttpStatusCode.OK);

        var interviewing = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/user-jobs?status=interviewing"));
        interviewing.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(uj);

        var prep = await PrepAsync(F, u, uj);
        prep.GetProperty("status").GetString().Should().Be("ready");
        prep.GetProperty("content").GetProperty("behavioural").EnumerateArray()
            .SelectMany(b => b.GetProperty("talkingPointsFromResume").EnumerateArray()).Should().NotBeEmpty();
        (await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}")))
            .GetProperty("status").GetString().Should().Be("interviewing", "prep does not move the card");
    }
}
