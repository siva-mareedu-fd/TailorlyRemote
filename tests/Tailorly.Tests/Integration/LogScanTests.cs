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

    /// <summary>
    /// The M3 flows (enrichment, scoring, scheduled digest, email, unsubscribe, notification-email confirmation) with
    /// verbose logging: no job text, model reply, email address, role or link token is logged.
    /// </summary>
    [Fact]
    public async Task No_job_text_model_reply_address_or_link_token_reaches_the_logs_in_the_digest_flow()
    {
        await using var f = new TailorlyAppFactory(fx.ConnectionString, new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Verbose",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Debug",
            ["Serilog:MinimumLevel:Override:Hangfire"] = "Debug",
        });
        const string jobSentinel = "SENTINEL-JOB-TEXT-4471";
        const string summarySentinel = "SENTINEL-ENRICH-8830";
        const string reasonSentinel = "SENTINEL-REASON-2290";
        var india = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var schedule = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, india).AddMinutes(-1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var role = MatchingTestData.NewRole();
        var u = await MatchingTestData.CreateMatcherAsync(f, role, "sentinel-digest", p =>
        {
            p["scheduleEnabled"] = true;
            p["scheduleLocalTime"] = schedule;
        });
        var alerts = TestUsers.NewEmail("sentinel-alerts");
        (await u.Api.PutAsync("/api/v1/me", new { displayName = "Test User", timeZone = "Asia/Kolkata", notificationEmail = alerts }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await MatchingTestData.InsertJobAsync(f, $"{role} Engineer", description: MatchingTestData.Description + " " + jobSentinel);
        f.Ai.Enqueue(AiTaskNames.JobEnrichment, System.Text.Json.JsonSerializer.Serialize(new
        {
            skills_required = new[] { "C#" }, skills_preferred = Array.Empty<string>(), skill_aliases = Array.Empty<object>(),
            seniority = "senior", years_experience_min = 5, years_experience_max = (int?)null, employment_type = "full_time",
            work_mode = "remote", eligibility = "worldwide", eligibility_evidence = jobSentinel, restricted_regions = Array.Empty<string>(),
            timezone_requirement = (string?)null, hiring_model_hint = "employee", salary_extracted = (object?)null, summary = summarySentinel,
        }));
        f.Ai.Enqueue(AiTaskNames.JobFitScore, System.Text.Json.JsonSerializer.Serialize(new
        {
            fit_score = 9, reasons = new[] { reasonSentinel }, matched_skills = new[] { "C#" }, missing_skills = Array.Empty<string>(),
            confidence = "high",
        }));

        (await MatchingTestData.DispatchAsync(f, DateTimeOffset.UtcNow)).Should().NotBeEmpty();
        await f.Jobs.RunAllAsync();
        var digestMail = f.Emails.To(u.Email).Single(m => m.ListUnsubscribeUrl is not null);
        System.Net.WebUtility.HtmlDecode(digestMail.HtmlBody).Should().Contain(reasonSentinel);
        var unsubscribeToken = Uri.UnescapeDataString(digestMail.ListUnsubscribeUrl!.Split("token=")[1]);
        var verifyLink = System.Text.RegularExpressions.Regex.Match(f.Emails.To(alerts).Single().TextBody,
            @"/verify-notification-email\?token=([^\s""<]+)");
        verifyLink.Success.Should().BeTrue();
        var verifyToken = Uri.UnescapeDataString(verifyLink.Groups[1].Value);
        (await u.Api.PostAsync("/api/v1/me/notification-email/verify", new { token = verifyToken })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var anon = f.CreateHttpsClient();
        (await anon.PostAsync(new Uri(digestMail.ListUnsubscribeUrl!).PathAndQuery, null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var logs = f.Logs.Lines.ToList();
        logs.Should().Contain(l => l.Contains("emailed", StringComparison.Ordinal), "the send itself is logged (ids and counts)");
        foreach (var secret in new[] { jobSentinel, summarySentinel, reasonSentinel, u.Email, alerts, role, unsubscribeToken, verifyToken,
                     "payment infrastructure" })
            logs.Where(l => l.Contains(secret, StringComparison.OrdinalIgnoreCase)).Should().BeEmpty($"'{secret[..Math.Min(12, secret.Length)]}…' must never be logged");
    }
}
