using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Ai;
using Tailorly.App.Features.Resumes;
using Tailorly.Core.Ports;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>ApiDesign §4.4 résumé endpoints and the ParseResume job, including its failure paths.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ResumeTests(PostgresAppFixture fx)
{
    private TailorlyAppFactory F => fx.Factory;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Get(TestUser u, Guid id) => await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/resumes/{id}"));

    [Fact]
    public async Task New_versions_become_active_when_ready_and_older_ones_can_be_reactivated_or_deleted()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "versions");
        var v1 = await TestUsers.UploadParsedResumeAsync(F, u, fileName: "first.docx");
        var v2 = await TestUsers.UploadParsedResumeAsync(F, u, fileName: "second.docx");

        (await Get(u, v1)).GetProperty("isActive").GetBoolean().Should().BeFalse();
        (await Get(u, v2)).GetProperty("isActive").GetBoolean().Should().BeTrue();
        (await Get(u, v2)).GetProperty("version").GetInt32().Should().Be(2);

        var list = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/resumes"));
        list.EnumerateArray().Select(r => r.GetProperty("originalFilename").GetString()).Should().Equal("second.docx", "first.docx");

        var act = await u.Api.PostAsync($"/api/v1/resumes/{v1}/activate");
        act.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Get(u, v1)).GetProperty("isActive").GetBoolean().Should().BeTrue();
        (await Get(u, v2)).GetProperty("isActive").GetBoolean().Should().BeFalse();

        (await u.Api.DeleteAsync($"/api/v1/resumes/{v2}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await u.Api.GetAsync($"/api/v1/resumes/{v2}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await u.Api.GetAsync($"/api/v1/resumes/{v2}/profile")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Db.ScalarAsync<long>(fx.ConnectionString, "SELECT count(*) FROM resume_profiles WHERE resume_id = @id", ("id", v2)))
            .Should().Be(0, "the profile cascades with the résumé");
        Directory.Exists(Path.Combine(F.StorageRoot, u.Id.ToString("D"), "resumes", v2.ToString("D"))).Should().BeFalse();
    }

    [Fact]
    public async Task Download_returns_the_original_bytes_as_an_attachment()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "download");
        var bytes = DocxBuilder.SampleResume();
        var id = await TestUsers.UploadParsedResumeAsync(F, u, bytes, "Asha CV.docx");

        var res = await u.Api.GetAsync($"/api/v1/resumes/{id}/download");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(bytes);
        res.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        res.Content.Headers.ContentDisposition.FileNameStar.Should().Be("Asha CV.docx");
        res.Content.Headers.ContentType!.MediaType.Should().Be(ResumeEndpoints.DocxContentType);
        res.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        res.Headers.CacheControl!.NoStore.Should().BeTrue();

        var sha = (await Db.StringsAsync(fx.ConnectionString, "SELECT sha256 FROM resumes WHERE id = @id", ("id", id))).Single();
        sha.Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        var key = (await Db.StringsAsync(fx.ConnectionString, "SELECT storage_key FROM resumes WHERE id = @id", ("id", id))).Single();
        key.Should().Be($"{u.Id:D}/resumes/{id:D}/original.docx");
    }

    [Fact]
    public async Task Only_a_ready_version_can_be_activated()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "notready");
        var res = await u.Api.UploadAsync("/api/v1/resumes", "r.docx", DocxBuilder.SampleResume());
        var id = (await ApiClient.JsonAsync(res)).GetProperty("id").GetGuid();
        (await u.Api.PostAsync($"/api/v1/resumes/{id}/activate")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        await F.Jobs.RunAllAsync();
    }

    [Fact]
    public async Task A_repeated_upload_while_parsing_returns_the_in_flight_resume_with_200()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "inflight");
        var first = await u.Api.UploadAsync("/api/v1/resumes", "a.docx", DocxBuilder.SampleResume());
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var again = await u.Api.UploadAsync("/api/v1/resumes", "b.docx", DocxBuilder.SampleResume());
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiClient.JsonAsync(again)).GetProperty("id").GetGuid().Should().Be((await ApiClient.JsonAsync(first)).GetProperty("id").GetGuid());
        await F.Jobs.RunAllAsync();
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/resumes"))).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Upload_requires_current_consent()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "noconsent");
        await Db.ExecAsync(fx.ConnectionString, "UPDATE user_profiles SET consent_version = '2020-01' WHERE user_id = @u", ("u", u.Id));
        var res = await u.Api.UploadAsync("/api/v1/resumes", "r.docx", DocxBuilder.SampleResume());
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ApiClient.CodeAsync(res)).Should().Be("CONSENT_REQUIRED");
    }

    [Fact]
    public async Task Upload_is_refused_with_409_when_the_documents_budget_is_used_up()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "budget");
        await Db.SetUsedAsync(fx.ConnectionString, u.Id, "documents", 1_400_000, 1_400_000);

        var res = await u.Api.UploadAsync("/api/v1/resumes", "r.docx", DocxBuilder.SampleResume());
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await ApiClient.JsonAsync(res);
        body.GetProperty("code").GetString().Should().Be("BUDGET_EXCEEDED");
        body.GetProperty("extra").GetProperty("bucket").GetString().Should().Be("documents");
        body.GetProperty("extra").GetProperty("resetsOn").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-01$");
        body.GetProperty("detail").GetString().Should().Contain("Documents budget resets on");
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/resumes"))).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Parse_fails_with_BUDGET_EXCEEDED_when_the_budget_runs_out_before_the_job()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "budgetjob");
        var res = await u.Api.UploadAsync("/api/v1/resumes", "r.docx", DocxBuilder.SampleResume());
        var id = (await ApiClient.JsonAsync(res)).GetProperty("id").GetGuid();
        await Db.SetUsedAsync(fx.ConnectionString, u.Id, "documents", 1_400_001, 1_400_000);
        var calls = F.Ai.Requests.Count;

        await F.Jobs.RunAllAsync();

        var r = await Get(u, id);
        r.GetProperty("parseStatus").GetString().Should().Be("failed");
        r.GetProperty("errorCode").GetString().Should().Be("BUDGET_EXCEEDED");
        F.Ai.Requests.Count.Should().Be(calls, "no model call is made without budget");
    }

    [Fact]
    public async Task Invalid_model_output_is_repaired_once_and_both_calls_are_charged()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "repair");
        F.Ai.Enqueue(AiTaskNames.ResumeParse, "{\"contact\": \"not an object\"}");
        var id = await TestUsers.UploadParsedResumeAsync(F, u);

        (await Get(u, id)).GetProperty("parseStatus").GetString().Should().Be("ready");
        var repair = F.Ai.Requests.Last(r => r.Task == AiTaskNames.ResumeParse);
        repair.Messages.Select(m => m.Role).Should().Equal(ModelRole.User, ModelRole.Assistant, ModelRole.User);
        repair.Messages[2].Text.Should().Contain("did not pass validation");

        var rows = await Db.StringsAsync(fx.ConnectionString,
            "SELECT success::text || ':' || coalesce(error_code,'') || ':' || counted_tokens FROM ai_usage WHERE user_id = @u ORDER BY id",
            ("u", u.Id));
        rows.Should().HaveCount(2);
        rows[0].Should().StartWith("false:AI_INVALID_OUTPUT:");
        rows[1].Should().StartWith("true::");
        var counted = rows.Sum(r => long.Parse(r.Split(':')[2], System.Globalization.CultureInfo.InvariantCulture));
        (await Db.ScalarAsync<long>(fx.ConnectionString, "SELECT used_tokens FROM user_budget_counters WHERE user_id = @u AND bucket = 'documents'",
            ("u", u.Id))).Should().Be(counted);
    }

    [Fact]
    public async Task Output_that_stays_invalid_after_the_repair_fails_with_AI_INVALID_OUTPUT()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "invalid");
        F.Ai.Enqueue(AiTaskNames.ResumeParse, "not json at all");
        F.Ai.Enqueue(AiTaskNames.ResumeParse, "{\"still\": \"wrong\"}");
        var id = await TestUsers.UploadParsedResumeAsync(F, u);

        var r = await Get(u, id);
        r.GetProperty("parseStatus").GetString().Should().Be("failed");
        r.GetProperty("errorCode").GetString().Should().Be("AI_INVALID_OUTPUT");
        r.GetProperty("isActive").GetBoolean().Should().BeFalse();
        (await u.Api.GetAsync($"/api/v1/resumes/{id}/profile")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Provider_failure_on_the_last_attempt_fails_with_AI_UNAVAILABLE()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "unavailable");
        F.Ai.Enqueue(AiTaskNames.ResumeParse, _ => throw new ModelProviderException("overloaded", transient: true, 529));
        var id = await TestUsers.UploadParsedResumeAsync(F, u);

        var r = await Get(u, id);
        r.GetProperty("parseStatus").GetString().Should().Be("failed");
        r.GetProperty("errorCode").GetString().Should().Be("AI_UNAVAILABLE");
    }

    [Fact]
    public async Task A_docx_without_text_fails_cleanly()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "empty");
        var id = await TestUsers.UploadParsedResumeAsync(F, u, new DocxBuilder().Para(" ").Build());
        var r = await Get(u, id);
        r.GetProperty("parseStatus").GetString().Should().Be("failed");
        r.GetProperty("errorCode").GetString().Should().Be("UNSUPPORTED_FILE_TYPE");
    }

    [Fact]
    public async Task Profile_edits_are_validated_against_the_schema_and_can_be_cleared()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "edits");
        var id = await TestUsers.UploadParsedResumeAsync(F, u);

        var bad = await u.Api.PutAsync($"/api/v1/resumes/{id}/profile", new
        {
            userEdited = new { skills = new[] { new { name = "C#", category = "not-a-category" } }, totalYears = 99 },
        });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var cleared = await u.Api.PutAsync($"/api/v1/resumes/{id}/profile", new { userEdited = (object?)null });
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiClient.JsonAsync(cleared)).GetProperty("userEdited").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Stuck_parses_older_than_30_minutes_are_marked_TIMEOUT_by_the_sweep()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "sweep");
        var res = await u.Api.UploadAsync("/api/v1/resumes", "r.docx", DocxBuilder.SampleResume());
        var id = (await ApiClient.JsonAsync(res)).GetProperty("id").GetGuid();
        F.Jobs.Clear(); // the worker "crashed"
        await Db.ExecAsync(fx.ConnectionString, "UPDATE resumes SET parse_status = 'running', created_at = now() - interval '31 minutes' WHERE id = @id", ("id", id));

        await using (var scope = F.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<SweepStuckWorkJob>().RunAsync(Ct);

        var r = await Get(u, id);
        r.GetProperty("parseStatus").GetString().Should().Be("failed");
        r.GetProperty("errorCode").GetString().Should().Be("TIMEOUT");

        // The user can retry: a new upload is accepted now that nothing is in flight.
        (await u.Api.UploadAsync("/api/v1/resumes", "r2.docx", DocxBuilder.SampleResume())).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await F.Jobs.RunAllAsync();
    }
}
