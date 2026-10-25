using System.Net;
using FluentAssertions;
using Tailorly.App.Ai;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>
/// PRD Journey A up to profile review (M1 exit criterion): sign up → verify email → consent (given at sign-up) →
/// upload .docx → parse (fake provider) → review and correct the parsed profile.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class JourneyATests(PostgresAppFixture fx)
{
    private TailorlyAppFactory F => fx.Factory;

    [Fact]
    public async Task Sign_up_verify_upload_parse_and_review_the_profile()
    {
        var api = await F.CreateApiClientAsync();
        var email = TestUsers.NewEmail("journey");

        // 1. Sign up → 201 and a verification email
        var userId = await TestUsers.RegisterAsync(F, api, email, "Asha Verma");
        F.Emails.To(email).Should().ContainSingle(m => m.Subject.Contains("Verify", StringComparison.Ordinal));

        // 2. Sign in before verifying: allowed, but protected actions say EMAIL_NOT_VERIFIED
        await TestUsers.LoginAsync(api, email);
        var me = await ApiClient.JsonAsync(await api.GetAsync("/api/v1/me"));
        me.GetProperty("emailVerified").GetBoolean().Should().BeFalse();
        me.GetProperty("consent").GetProperty("version").GetString().Should().Be(TestUsers.ConsentVersion);
        var blocked = await api.UploadAsync("/api/v1/resumes", "resume.docx", DocxBuilder.SampleResume());
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ApiClient.CodeAsync(blocked)).Should().Be("EMAIL_NOT_VERIFIED");

        // 3. Verify from the email link
        await TestUsers.VerifyFromEmailAsync(F, api, email);
        (await ApiClient.JsonAsync(await api.GetAsync("/api/v1/me"))).GetProperty("emailVerified").GetBoolean().Should().BeTrue();

        // 4. Upload → 202 queued with Location
        var upload = await api.UploadAsync("/api/v1/resumes", "asha_resume.docx", DocxBuilder.SampleResume());
        upload.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var resume = await ApiClient.JsonAsync(upload);
        var id = resume.GetProperty("id").GetGuid();
        upload.Headers.Location!.ToString().Should().Be($"/api/v1/resumes/{id}");
        resume.GetProperty("parseStatus").GetString().Should().Be("queued");
        resume.GetProperty("version").GetInt32().Should().Be(1);
        resume.GetProperty("isActive").GetBoolean().Should().BeFalse();

        // 5. The parse job runs; polling shows ready + active + format findings
        await F.Jobs.RunAllAsync();
        var polled = await ApiClient.JsonAsync(await api.GetAsync($"/api/v1/resumes/{id}"));
        polled.GetProperty("parseStatus").GetString().Should().Be("ready");
        polled.GetProperty("errorCode").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        polled.GetProperty("isActive").GetBoolean().Should().BeTrue();
        var findings = polled.GetProperty("formatFindings");
        findings.GetProperty("editableParagraphs").GetInt32().Should().BeGreaterThan(0);
        findings.GetProperty("lockedParagraphs").GetInt32().Should().BeGreaterThan(0);
        findings.GetProperty("hasTables").GetBoolean().Should().BeFalse();

        // The model saw the résumé text only inside the untrusted-data delimiters.
        var request = F.Ai.Requests.Last(r => r.Task == AiTaskNames.ResumeParse);
        request.Messages[0].Text.Should().StartWith("<resume_data>").And.Contain("Built REST APIs for the payments team");
        request.Model.Should().Be("claude-sonnet-5-5", "the model comes from ai_task_config");

        // 6. Review the parsed profile
        var profile = await ApiClient.JsonAsync(await api.GetAsync($"/api/v1/resumes/{id}/profile"));
        profile.GetProperty("parsed").GetProperty("totalYears").GetDouble().Should().Be(6);
        profile.GetProperty("userEdited").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        profile.GetProperty("effective").GetProperty("skills").GetArrayLength().Should().Be(4);

        // 7. Correct it: add a skill, fix the years
        var put = await api.PutAsync($"/api/v1/resumes/{id}/profile", new
        {
            userEdited = new
            {
                skills = new[]
                {
                    new { name = "C#", category = "language" }, new { name = ".NET", category = "framework" },
                    new { name = "PostgreSQL", category = "database" }, new { name = "Docker", category = "devops" },
                },
                totalYears = 7.5,
            },
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(put));
        var edited = await ApiClient.JsonAsync(put);
        edited.GetProperty("effective").GetProperty("totalYears").GetDouble().Should().Be(7.5);
        edited.GetProperty("effective").GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("name").GetString())
            .Should().Contain("Docker");
        edited.GetProperty("parsed").GetProperty("totalYears").GetDouble().Should().Be(6, "the parsed original is kept");
        edited.GetProperty("effective").GetProperty("experience").GetArrayLength().Should().Be(2, "uncorrected fields come from parsed");

        // 8. Usage shows the Documents tokens the parse spent
        var usage = await ApiClient.JsonAsync(await api.GetAsync("/api/v1/usage"));
        var documents = usage.GetProperty("buckets").EnumerateArray().Single(b => b.GetProperty("bucket").GetString() == "documents");
        documents.GetProperty("used").GetInt64().Should().BeGreaterThan(0);
        documents.GetProperty("limit").GetInt64().Should().Be(1_400_000);
        userId.Should().NotBeEmpty();
    }
}
