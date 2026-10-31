using System.Net;
using System.Text.Json;
using FluentAssertions;
using Tailorly.App.Ai;
using Tailorly.Docx;

namespace Tailorly.Tests.Infrastructure;

/// <summary>Arranging tracked jobs, analyses and tailorings for the M4 tests (on top of <see cref="MatchingTestData"/>).</summary>
public static class TailoringTestData
{
    /// <summary>A user with <see cref="DocxBuilder.SampleResume"/> parsed, a job scored for them and tracked: (user, jobId, userJobId).</summary>
    public static async Task<(TestUser User, Guid JobId, Guid UserJobId)> TrackedJobAsync(TailorlyAppFactory f, string tag = "tailor",
        string? description = MatchingTestData.Description)
    {
        var role = MatchingTestData.NewRole();
        var u = await MatchingTestData.CreateMatcherAsync(f, role, tag);
        // A second résumé upload so tailoring runs on the real sample (the matcher's profile reply stays as parsed).
        MatchingTestData.Reply(f, AiTaskNames.ResumeParse, SampleProfile());
        await TestUsers.UploadParsedResumeAsync(f, u);
        var jobId = await MatchingTestData.InsertJobAsync(f, $"Senior {role} Engineer", company: "Acme Cloud", description: description);
        MatchingTestData.Reply(f, AiTaskNames.JobEnrichment, MatchingTestData.Enrichment());
        await MatchingTestData.RunNowAsync(f, u);
        var res = await u.Api.PostAsync("/api/v1/user-jobs", new { jobId });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await ApiClient.TextAsync(res));
        return (u, jobId, (await ApiClient.JsonAsync(res)).GetProperty("id").GetGuid());
    }

    /// <summary>The parsed profile of <see cref="DocxBuilder.SampleResume"/>.</summary>
    public static object SampleProfile() => new
    {
        contact = new { name = "Asha Verma", email = "asha@example.test", phone = "+91 90000 00000", location = "Hyderabad, India", links = Array.Empty<string>() },
        summary = "Backend engineer with 6 years of experience building .NET services for payments.",
        skills = new[] { new { name = "C#", category = "language" }, new { name = "SQL", category = "language" },
            new { name = ".NET", category = "framework" }, new { name = "PostgreSQL", category = "database" }, new { name = "Docker", category = "tool" } },
        experience = new[]
        {
            new { company = "Example Corp", title = "Senior Software Engineer", start = "2021-01", end = (string?)null,
                bullets = new[] { "Built REST APIs for the payments team using C# and PostgreSQL." } },
            new { company = "Sample Systems", title = "Software Engineer", start = "2018-07", end = (string?)"2020-12",
                bullets = new[] { "Maintained billing services and wrote integration tests for every release." } },
        },
        education = new[] { new { institution = "Example University", degree = "B.Tech", field = "Computer Science", start = (string?)null, end = (string?)"2018" } },
        certifications = new[] { new { name = "Microsoft Certified: Azure Developer Associate", issuer = (string?)"Microsoft", year = (string?)null } },
        totalYears = 6,
        domains = new[] { "payments" },
    };

    /// <summary>The paragraph id of the sample résumé's paragraph starting with <paramref name="startsWith"/>.</summary>
    public static string Pid(string startsWith) =>
        ResumeExtractor.Extract(new MemoryStream(DocxBuilder.SampleResume())).Paragraphs
            .Single(p => p.Text.StartsWith(startsWith, StringComparison.Ordinal)).Id;

    /// <param name="kubernetesEvidence">The paragraph that shows Kubernetes (for a re-score after it was added).</param>
    public static object Analysis(string kubernetesStatus = "missing", string? kubernetesEvidence = null) => new
    {
        requirements = new object[]
        {
            new { text = "5+ years of C# and .NET", type = "required", status = "met", evidence_paragraph_ids = new[] { Pid("Backend engineer"), Pid("Built REST") } },
            new { text = "PostgreSQL", type = "required", status = "met", evidence_paragraph_ids = new[] { Pid("Built REST") } },
            new { text = "Kubernetes", type = "preferred", status = kubernetesStatus,
                evidence_paragraph_ids = kubernetesEvidence is null ? Array.Empty<string>() : [Pid(kubernetesEvidence)] },
        },
        title_seniority_label = 0.8,
        experience_relevance = new { score = 0.75, justification = "Payments backend work matches the role." },
        gaps = new[] { new { skill = "Kubernetes", likely_has_based_on_profile = true } },
    };

    public static object Edit(string paragraph, string newText, string rationale = "Uses the job's terms.", bool usesConfirmed = false) =>
        new { paragraph_id = Pid(paragraph), new_text = newText, rationale, uses_confirmed_fact = usesConfirmed };

    public static object Tailor(params object[] edits) => new { edits, unaddressed_gaps = new[] { "Kubernetes" } };

    public static object Letter(string tone = "concise", params string[] paragraphs) => new
    {
        paragraphs = paragraphs.Length > 0 ? paragraphs :
        [
            "Hello Acme Cloud team,",
            "I build .NET and PostgreSQL services for payments teams, which is the heart of this role.",
            "At Example Corp I built REST APIs for the payments team using C# and PostgreSQL.",
            "I would be glad to talk about how I can help.",
            "Best regards,",
            "Asha Verma",
        ],
        word_count = 60,
        tone,
    };

    /// <summary>Starts the analysis, runs the jobs and returns it.</summary>
    public static async Task<JsonElement> AnalyseAsync(TailorlyAppFactory f, TestUser u, Guid userJobId, object? reply = null)
    {
        MatchingTestData.Reply(f, AiTaskNames.AtsAnalysis, reply ?? Analysis());
        var res = await u.Api.PostAsync($"/api/v1/user-jobs/{userJobId}/analysis", new { });
        res.StatusCode.Should().Be(HttpStatusCode.Accepted, await ApiClient.TextAsync(res));
        await f.Jobs.RunAllAsync();
        return await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{userJobId}/analysis"));
    }

    /// <summary>Starts a tailoring with a scripted reply, runs the jobs and returns the document.</summary>
    public static async Task<JsonElement> TailorAsync(TailorlyAppFactory f, TestUser u, Guid userJobId, object reply)
    {
        MatchingTestData.Reply(f, AiTaskNames.ResumeTailor, reply);
        var res = await u.Api.PostAsync($"/api/v1/user-jobs/{userJobId}/tailored-resumes", new { });
        res.StatusCode.Should().Be(HttpStatusCode.Accepted, await ApiClient.TextAsync(res));
        var id = (await ApiClient.JsonAsync(res)).GetProperty("id").GetGuid();
        await f.Jobs.RunAllAsync();
        return await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/tailored-documents/{id}"));
    }

    public static IEnumerable<JsonElement> Edits(JsonElement doc) => doc.GetProperty("edits").EnumerateArray();

    public static JsonElement EditFor(JsonElement doc, string paragraph) =>
        Edits(doc).Single(e => e.GetProperty("paragraphId").GetString() == Pid(paragraph));

    public static Task<HttpResponseMessage> DecideAsync(TestUser u, Guid docId, params (string EditId, string Decision)[] decisions) =>
        u.Api.PostAsync($"/api/v1/tailored-documents/{docId}/decisions",
            new { decisions = decisions.Select(d => new { editId = d.EditId, decision = d.Decision }) });
}
