using System.Net;
using FluentAssertions;
using Tailorly.App.Ai;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>
/// Hard rule: never log résumé text, prompts, model responses, emails or tokens (CLAUDE.md, SDD §14.3). Runs the M1
/// flows with verbose logging (EF Core and ASP.NET at Debug) and scans every captured event, rendered, for sentinels.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LogScanTests(PostgresAppFixture fx)
{
    [Fact]
    public async Task No_resume_text_prompt_reply_email_password_or_token_reaches_the_logs()
    {
        await using var f = new TailorlyAppFactory(fx.ConnectionString, new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Verbose",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Debug",
            ["Serilog:MinimumLevel:Override:Hangfire"] = "Debug",
        });

        const string resumeSentinel = "SENTINEL-RESUME-7731 quartz zebra";
        const string replySentinel = "SENTINEL-AI-REPLY-5512";
        const string password = "sentinel passphrase 9981";
        const string name = "Sentinel Name 4410";
        var email = TestUsers.NewEmail("sentinel-logs");

        var api = await f.CreateApiClientAsync();
        (await api.PostAsync("/api/v1/auth/register", new
        {
            email, password, displayName = name, timeZone = "Asia/Kolkata", consentVersion = TestUsers.ConsentVersion,
        })).StatusCode.Should().Be(HttpStatusCode.Created);
        await f.Jobs.RunAllAsync();
        var verifyMail = f.Emails.To(email).Single();
        var (userId, verifyToken) = ApiClient.ParseLink(verifyMail.TextBody, "/verify-email");
        (await api.PostAsync("/api/v1/auth/verify-email", new { userId, token = verifyToken })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.PostAsync("/api/v1/auth/login", new { email, password = "wrong " + password })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await TestUsers.LoginAsync(api, email, password);

        // Upload a résumé carrying the sentinel; the scripted model reply carries another one.
        var reply = $$"""
            {"contact":{"name":"{{name}}","email":"{{email}}","phone":null,"location":"Hyderabad","links":[]},
             "summary":"{{replySentinel}}","skills":[{"name":"C#","category":"language"}],
             "experience":[{"company":"Example Corp","title":"Engineer","start":"2021-01","end":null,"bullets":["{{replySentinel}}"]}],
             "education":[],"certifications":[],"totalYears":5,"domains":[]}
            """;
        f.Ai.Enqueue(AiTaskNames.ResumeParse, "not json " + replySentinel); // forces a repair round trip too
        f.Ai.Enqueue(AiTaskNames.ResumeParse, reply);
        var upload = await api.UploadAsync("/api/v1/resumes", $"{name}.docx", DocxBuilder.SampleResume(resumeSentinel));
        upload.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var resumeId = (await ApiClient.JsonAsync(upload)).GetProperty("id").GetGuid();
        await f.Jobs.RunAllAsync();
        (await ApiClient.TextAsync(await api.GetAsync($"/api/v1/resumes/{resumeId}/profile"))).Should().Contain(replySentinel);

        // A rejected upload, a password reset and a deletion request add their own log lines.
        (await api.UploadAsync("/api/v1/resumes", "macro.docx", UnsafeFiles.WithMacroPart())).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await api.PostAsync("/api/v1/auth/forgot-password", new { email })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.DeleteAsync("/api/v1/account")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await f.Jobs.RunAllAsync();
        var resetToken = ApiClient.ParseLink(f.Emails.To(email).Single(m => m.Subject.Contains("Reset", StringComparison.Ordinal)).TextBody,
            "/reset-password").Token;

        var logs = f.Logs.Lines.ToList();
        logs.Should().NotBeEmpty();
        logs.Should().Contain(l => l.Contains("parsed", StringComparison.Ordinal), "the parse itself is logged (ids and counts)");

        var secrets = new[] { resumeSentinel, "SENTINEL-RESUME-7731", replySentinel, password, name, email, verifyToken, resetToken,
            "Built REST APIs for the payments team" };
        foreach (var secret in secrets)
            logs.Where(l => l.Contains(secret, StringComparison.OrdinalIgnoreCase)).Should().BeEmpty($"'{secret[..Math.Min(12, secret.Length)]}…' must never be logged");
    }
}
