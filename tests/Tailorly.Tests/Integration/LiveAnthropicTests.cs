using System.Net;
using System.Text.Json;
using FluentAssertions;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>
/// Journey A with the real Anthropic provider (M1 exit criterion). Skipped unless <c>TAILORLY_LIVE_ANTHROPIC_KEY</c>
/// is set; it sends one synthetic résumé (no real person's data) and spends a few thousand Documents tokens.
/// With <c>TAILORLY_RECORD_FIXTURES=1</c> the parsed reply is saved as the fake provider's recorded fixture.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LiveAnthropicTests(PostgresAppFixture fx)
{
    [Fact]
    public async Task Resume_parse_works_end_to_end_with_the_real_provider()
    {
        var key = Environment.GetEnvironmentVariable("TAILORLY_LIVE_ANTHROPIC_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Assert.Skip("Set TAILORLY_LIVE_ANTHROPIC_KEY to run against the real Anthropic API.");
            return;
        }

        await using var f = new TailorlyAppFactory(fx.ConnectionString, new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "Anthropic",
            ["Anthropic:ApiKey"] = key,
        });
        var u = await TestUsers.CreateVerifiedAsync(f, "live");
        var id = await TestUsers.UploadParsedResumeAsync(f, u);

        var resume = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/resumes/{id}"));
        resume.GetProperty("parseStatus").GetString().Should().Be("ready", $"errorCode: {resume.GetProperty("errorCode")}");
        resume.GetProperty("isActive").GetBoolean().Should().BeTrue();

        var profileRes = await u.Api.GetAsync($"/api/v1/resumes/{id}/profile");
        profileRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var parsed = (await ApiClient.JsonAsync(profileRes)).GetProperty("parsed");
        parsed.GetProperty("experience").GetArrayLength().Should().Be(2);
        parsed.GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("name").GetString()).Should().Contain("PostgreSQL");
        parsed.GetProperty("experience")[0].GetProperty("company").GetString().Should().Contain("Example Corp");

        var usage = await Db.StringsAsync(fx.ConnectionString,
            "SELECT model || ' ' || success || ' ' || counted_tokens FROM ai_usage WHERE user_id = @u", ("u", u.Id));
        usage.Should().NotBeEmpty().And.Contain(r => r.Contains(" true ", StringComparison.Ordinal));

        if (Environment.GetEnvironmentVariable("TAILORLY_RECORD_FIXTURES") == "1")
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "Tailorly.App", "Infrastructure", "Ai", "Fixtures", "resume_parse.json"));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// The M4 flow with the real provider: run (enrichment and fit), track, ATS analysis, tailoring, finalising with a
    /// re-score, and a cover letter. Checks the real model's replies pass the production validators (no
    /// <c>AI_INVALID_OUTPUT</c>) and that the results respect the limits. Spends roughly 30–40K tokens.
    /// </summary>
    [Fact]
    public async Task Analysis_tailoring_and_cover_letter_work_end_to_end_with_the_real_provider()
    {
        var key = Environment.GetEnvironmentVariable("TAILORLY_LIVE_ANTHROPIC_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Assert.Skip("Set TAILORLY_LIVE_ANTHROPIC_KEY to run against the real Anthropic API.");
            return;
        }

        await using var f = new TailorlyAppFactory(fx.ConnectionString, new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "Anthropic",
            ["Anthropic:ApiKey"] = key,
        });
        var u = await TestUsers.CreateVerifiedAsync(f, "live-tailor");
        await TestUsers.UploadParsedResumeAsync(f, u);
        await MatchingTestData.SetPreferencesAsync(u, p => p["targetRoles"] = new[] { "Backend Engineer" });
        var jobId = await MatchingTestData.InsertJobAsync(f, "Senior Backend Engineer (.NET)", company: "Acme Cloud");
        await MatchingTestData.RunNowAsync(f, u);
        var tracked = await u.Api.PostAsync("/api/v1/user-jobs", new { jobId });
        tracked.StatusCode.Should().Be(HttpStatusCode.Created, await ApiClient.TextAsync(tracked));
        var uj = (await ApiClient.JsonAsync(tracked)).GetProperty("id").GetGuid();
        var log = new List<string>();

        (await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/analysis", new { })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await f.Jobs.RunAllAsync();
        var analysis = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}/analysis"));
        analysis.GetProperty("status").GetString().Should().Be("ready", $"errorCode: {analysis.GetProperty("errorCode")}");
        log.Add($"analysis: ATS {analysis.GetProperty("atsScore")}, {analysis.GetProperty("requirements").GetArrayLength()} requirements, " +
                $"{analysis.GetProperty("gaps").GetArrayLength()} gaps");

        var start = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/tailored-resumes", new { });
        var docId = (await ApiClient.JsonAsync(start)).GetProperty("id").GetGuid();
        await f.Jobs.RunAllAsync();
        var doc = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/tailored-documents/{docId}"));
        doc.GetProperty("status").GetString().Should().Be("proposed", $"errorCode: {doc.GetProperty("errorCode")}");
        var edits = TailoringTestData.Edits(doc).ToList();
        log.Add($"tailoring: {edits.Count} edits, flags [{string.Join(", ", edits.SelectMany(e => e.GetProperty("flags").EnumerateArray().Select(x => x.GetString())))}]");
        var safe = edits.Where(e => e.GetProperty("flags").GetArrayLength() == 0).Select(e => (e.GetProperty("id").GetString()!, "accepted")).ToArray();
        if (safe.Length > 0)
        {
            (await TailoringTestData.DecideAsync(u, docId, safe)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await u.Api.PostAsync($"/api/v1/tailored-documents/{docId}/finalize")).StatusCode.Should().Be(HttpStatusCode.Accepted);
            await f.Jobs.RunAllAsync();
            var ready = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/tailored-documents/{docId}"));
            ready.GetProperty("status").GetString().Should().Be("ready", $"errorCode: {ready.GetProperty("errorCode")}");
            ready.GetProperty("atsAfterBasis").GetString().Should().Be("rescored");
            log.Add($"finalised: ATS {ready.GetProperty("atsBefore")} → {ready.GetProperty("atsAfter")}");
            (await u.Api.GetAsync($"/api/v1/tailored-documents/{docId}/download")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        foreach (var (tone, limit) in new[] { ("concise", 180), ("confident", 250) })
        {
            var letterStart = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { tone, notes = "Referred by Priya" });
            var letterId = (await ApiClient.JsonAsync(letterStart)).GetProperty("id").GetGuid();
            await f.Jobs.RunAllAsync();
            var letter = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/tailored-documents/{letterId}"));
            letter.GetProperty("status").GetString().Should().Be("ready", $"{tone} errorCode: {letter.GetProperty("errorCode")}");
            var words = letter.GetProperty("content").GetProperty("wordCount").GetInt32();
            words.Should().BeInRange(30, limit, tone);
            log.Add($"cover letter {tone}: {words} words");
        }

        var calls = await Db.StringsAsync(fx.ConnectionString,
            "SELECT task || ' ' || model || ' ' || success || ' ' || counted_tokens FROM ai_usage WHERE user_id = @u ORDER BY id", ("u", u.Id));
        log.AddRange(calls);
        foreach (var line in log) TestContext.Current.SendDiagnosticMessage(line);
        var report = Environment.GetEnvironmentVariable("TAILORLY_LIVE_REPORT");
        if (!string.IsNullOrEmpty(report)) await File.WriteAllLinesAsync(report, log, TestContext.Current.CancellationToken);
    }
}
