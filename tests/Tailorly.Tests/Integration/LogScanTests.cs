using System.Net;
using FluentAssertions;
using Tailorly.App.Ai;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>
/// Hard rule: never log résumé text, job descriptions, prompts, model responses, emails or tokens (CLAUDE.md, SDD §14.3).
/// Runs the main flows with verbose logging (everything at Verbose, EF Core and ASP.NET at Debug) and scans every
/// captured event, rendered with its properties and exception, for sentinels; then scans the rows that must stay
/// content-free too (<c>audit_log</c>, <c>ai_usage</c>). Each test also checks that the work itself was logged, so an
/// empty capture cannot pass.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LogScanTests(PostgresAppFixture fx)
{
    private static readonly Dictionary<string, string?> Verbose = new()
    {
        ["Serilog:MinimumLevel:Default"] = "Verbose",
        ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Debug",
        ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Debug",
        ["Serilog:MinimumLevel:Override:Hangfire"] = "Debug",
    };

    private static void ShouldNotContainAny(IEnumerable<string> lines, IEnumerable<string> secrets, string where)
    {
        var all = lines.ToList();
        foreach (var secret in secrets)
            all.Where(l => l.Contains(secret, StringComparison.OrdinalIgnoreCase)).Should()
                .BeEmpty($"'{secret[..Math.Min(12, secret.Length)]}…' must never reach {where}");
    }

    private async Task ShouldNotBePersistedAsync(IReadOnlyCollection<string> secrets)
    {
        ShouldNotContainAny(await PersistedText.AuditLogAsync(fx.ConnectionString), secrets, "audit_log");
        ShouldNotContainAny(await PersistedText.AiUsageAsync(fx.ConnectionString), secrets, "ai_usage");
    }

    [Fact]
    public async Task No_resume_text_prompt_reply_email_password_or_token_reaches_the_logs()
    {
        await using var f = new TailorlyAppFactory(fx.ConnectionString, Verbose);

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
        // The download carries the file name (usually the person's name) in Content-Disposition; ASP.NET logs it at Debug.
        var download = await api.GetAsync($"/api/v1/resumes/{resumeId}/download");
        download.Content.Headers.ContentDisposition!.FileNameStar.Should().Be($"{name}.docx");

        // A rejected upload, a password reset and a confirmed deletion (audited, then purged) add their own lines and rows.
        (await api.UploadAsync("/api/v1/resumes", "macro.docx", UnsafeFiles.WithMacroPart())).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await api.PostAsync("/api/v1/auth/forgot-password", new { email })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.DeleteAsync("/api/v1/account")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await f.Jobs.RunAllAsync();
        var resetToken = ApiClient.ParseLink(f.Emails.To(email).Single(m => m.Subject.Contains("Reset", StringComparison.Ordinal)).TextBody,
            "/reset-password").Token;
        var deletionToken = ApiClient.ParseLink(f.Emails.To(email).Single(m => m.TextBody.Contains("/confirm-deletion", StringComparison.Ordinal))
            .TextBody, "/confirm-deletion").Token;
        (await api.PostAsync("/api/v1/account/confirm-deletion", new { userId, token = deletionToken })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await f.Jobs.RunAllAsync();

        var logs = f.Logs.Lines.ToList();
        logs.Should().NotBeEmpty();
        logs.Should().Contain(l => l.Contains("parsed", StringComparison.Ordinal), "the parse itself is logged (ids and counts)");
        var audit = await PersistedText.AuditLogAsync(fx.ConnectionString);
        foreach (var action in new[] { "account.delete.requested", "account.delete.confirmed", "account.purged" })
            audit.Should().Contain(r => r.Contains(action, StringComparison.Ordinal) && r.Contains(userId.ToString(), StringComparison.Ordinal),
                "the deletion is audited by user id");

        var secrets = new[] { resumeSentinel, "SENTINEL-RESUME-7731", replySentinel, password, name, email, verifyToken, resetToken,
            deletionToken, "Built REST APIs for the payments team", $"{name}.docx" };
        ShouldNotContainAny(logs, secrets, "the logs");
        await ShouldNotBePersistedAsync(secrets);
    }

    /// <summary>
    /// The M4 flows (analysis, pasted description and its enrichment, tailoring with a repair turn, decisions, finalising,
    /// download, cover letter with notes, content edit) with verbose logging: no résumé or job text, model reply, edit,
    /// rationale, letter or note is logged.
    /// </summary>
    [Fact]
    public async Task No_resume_job_edit_letter_or_note_text_reaches_the_logs_in_the_tailoring_flow()
    {
        await using var f = new TailorlyAppFactory(fx.ConnectionString, Verbose);
        const string pasteSentinel = "SENTINEL-PASTED-JOB-6621";
        const string editSentinel = "sentineleditquartz"; // lower-case words: a number or a name would be refused or flagged
        const string rationaleSentinel = "SENTINEL-RATIONALE-9114";
        const string letterSentinel = "sentinelletterquartz";
        const string noteSentinel = "SENTINEL-NOTE-7745";
        const string justification = "SENTINEL-JUSTIFY-1402";

        var (u, _, uj) = await TailoringTestData.TrackedJobAsync(f, "sentinel-tailor");
        (await u.Api.PutAsync($"/api/v1/user-jobs/{uj}/description",
            new { description = MatchingTestData.Description + " " + pasteSentinel })).StatusCode.Should().Be(HttpStatusCode.OK);
        MatchingTestData.Reply(f, AiTaskNames.ByoJobEnrichment, MatchingTestData.Enrichment());
        var analysis = System.Text.Json.JsonSerializer.Serialize(TailoringTestData.Analysis()).Replace(
            "Payments backend work matches the role.", justification, StringComparison.Ordinal);
        f.Ai.Enqueue(AiTaskNames.AtsAnalysis, analysis);
        (await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/analysis", new { })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await f.Jobs.RunAllAsync();

        // An invented number forces a repair turn (whose prompt must not be logged either), then a valid edit.
        MatchingTestData.Reply(f, AiTaskNames.ResumeTailor, TailoringTestData.Tailor(
            TailoringTestData.Edit("Built REST", $"Built REST APIs for 9 teams {editSentinel}.", rationaleSentinel)));
        MatchingTestData.Reply(f, AiTaskNames.ResumeTailor, TailoringTestData.Tailor(
            TailoringTestData.Edit("Built REST", $"Built REST APIs in C# and PostgreSQL {editSentinel}.", rationaleSentinel)));
        var start = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/tailored-resumes", new { });
        var docId = (await ApiClient.JsonAsync(start)).GetProperty("id").GetGuid();
        await f.Jobs.RunAllAsync();
        var doc = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/tailored-documents/{docId}"));
        await TailoringTestData.DecideAsync(u, docId, (TailoringTestData.Edits(doc).Single().GetProperty("id").GetString()!, "accepted"));
        f.Ai.Enqueue(AiTaskNames.AtsAnalysis, analysis);
        (await u.Api.PostAsync($"/api/v1/tailored-documents/{docId}/finalize")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await f.Jobs.RunAllAsync();
        (await u.Api.GetAsync($"/api/v1/tailored-documents/{docId}/download")).StatusCode.Should().Be(HttpStatusCode.OK);

        MatchingTestData.Reply(f, AiTaskNames.CoverLetter, TailoringTestData.Letter("concise", "Hello Acme Cloud team,",
            $"I build .NET services {letterSentinel}.", "Best regards,", "Asha Verma"));
        var letter = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { notes = noteSentinel });
        var letterId = (await ApiClient.JsonAsync(letter)).GetProperty("id").GetGuid();
        await f.Jobs.RunAllAsync();
        (await ApiClient.TextAsync(await u.Api.GetAsync($"/api/v1/tailored-documents/{letterId}"))).Should().Contain(letterSentinel);
        (await u.Api.PutAsync($"/api/v1/tailored-documents/{letterId}/content", new { paragraphs = new[] { letterSentinel + " edited" } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var logs = f.Logs.Lines.ToList();
        logs.Should().Contain(l => l.Contains("finalised", StringComparison.Ordinal), "the work itself is logged (ids and counts)");
        var secrets = new[] { pasteSentinel, editSentinel, rationaleSentinel, letterSentinel, noteSentinel, justification, u.Email,
            "Built REST APIs", "payment infrastructure", "Asha Verma" };
        ShouldNotContainAny(logs, secrets, "the logs");
        await ShouldNotBePersistedAsync(secrets);
    }

    /// <summary>
    /// The M3 flows (enrichment, scoring, scheduled digest, email, unsubscribe, notification-email confirmation) with
    /// verbose logging: no job text, model reply, email address, role or link token is logged.
    /// </summary>
    [Fact]
    public async Task No_job_text_model_reply_address_or_link_token_reaches_the_logs_in_the_digest_flow()
    {
        await using var f = new TailorlyAppFactory(fx.ConnectionString, Verbose);
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
        // Routed by title: the shared database may hold other tests' un-enriched jobs, which the same runs pick up.
        MatchingTestData.ReplyByTitle(f, AiTaskNames.JobEnrichment, 60, (role, new
        {
            skills_required = new[] { "C#" }, skills_preferred = Array.Empty<string>(), skill_aliases = Array.Empty<object>(),
            seniority = "senior", years_experience_min = 5, years_experience_max = (int?)null, employment_type = "full_time",
            work_mode = "remote", eligibility = "worldwide", eligibility_evidence = jobSentinel, restricted_regions = Array.Empty<string>(),
            timezone_requirement = (string?)null, hiring_model_hint = "employee", salary_extracted = (object?)null, summary = summarySentinel,
        }));
        MatchingTestData.ReplyByTitle(f, AiTaskNames.JobFitScore, 20, (role, new
        {
            fit_score = 9, reasons = new[] { reasonSentinel }, matched_skills = new[] { "C#" }, missing_skills = Array.Empty<string>(),
            confidence = "high",
        }));

        // The dispatcher claims every due user in the shared database; only this user's runs consume the scripted replies.
        (await MatchingTestData.DispatchAsync(f, DateTimeOffset.UtcNow)).Should().NotBeEmpty();
        await f.Jobs.RunAllAsync(job => job.Args.Contains(u.Id));
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
        var secrets = new[] { jobSentinel, summarySentinel, reasonSentinel, u.Email, alerts, role, unsubscribeToken, verifyToken,
            "payment infrastructure" };
        ShouldNotContainAny(logs, secrets, "the logs");
        await ShouldNotBePersistedAsync(secrets);
    }

    /// <summary>
    /// Library exception messages can echo their input (an XML entity name, a JSON value, a provider error body), and the
    /// parse job and the request pipeline log whole exceptions. Drives a real System.Xml exception carrying a sentinel into
    /// both, and a provider failure whose inner exception quotes another into the AI runner: the exception types and stack
    /// are logged, the messages are not, and the client gets the generic 500 body.
    /// </summary>
    [Fact]
    public async Task Library_exception_messages_never_reach_the_logs_or_the_response()
    {
        var store = new FaultyFileStore();
        await using var f = new TailorlyAppFactory(fx.ConnectionString, Verbose, services: store.Install);
        const string readSentinel = "sentinelxmlquartz"; // an XML entity name, so letters only
        const string providerSentinel = "SENTINEL-PROVIDER-ECHO-3307";
        var u = await TestUsers.CreateVerifiedAsync(f, "sentinel-exceptions");

        // The parse job cannot read the stored file: it logs the exception and fails the résumé.
        store.ReadFault = () => FaultyFileStore.EchoingLibraryFailure(readSentinel);
        var unreadable = await u.Api.UploadAsync("/api/v1/resumes", "cv.docx", DocxBuilder.SampleResume());
        unreadable.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var unreadableId = (await ApiClient.JsonAsync(unreadable)).GetProperty("id").GetGuid();
        await f.Jobs.RunAllAsync();
        store.ReadFault = null;
        (await ApiClient.TextAsync(await u.Api.GetAsync($"/api/v1/resumes/{unreadableId}"))).Should().Contain("failed");

        // The provider fails with an SDK error that quotes the request.
        f.Ai.Enqueue(AiTaskNames.ResumeParse, _ => throw new Tailorly.Core.Ports.ModelProviderException(
            "The AI provider rejected the request.", transient: false,
            inner: new InvalidOperationException("400 invalid_request_error near: " + providerSentinel)));
        var rejected = await u.Api.UploadAsync("/api/v1/resumes", "cv.docx", DocxBuilder.SampleResume());
        var rejectedId = (await ApiClient.JsonAsync(rejected)).GetProperty("id").GetGuid();
        await f.Jobs.RunAllAsync();
        (await ApiClient.TextAsync(await u.Api.GetAsync($"/api/v1/resumes/{rejectedId}"))).Should().Contain("AI_UNAVAILABLE");

        // A download hits the read failure inside a request: unhandled, so a 500 with the generic body.
        var resumeId = await TestUsers.UploadParsedResumeAsync(f, u);
        store.ReadFault = () => FaultyFileStore.EchoingLibraryFailure(readSentinel);
        var download = await u.Api.GetAsync($"/api/v1/resumes/{resumeId}/download");
        store.ReadFault = null;
        download.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ApiClient.TextAsync(download)).Should().NotContain(readSentinel).And.Contain("INTERNAL_ERROR");

        var logs = f.Logs.Lines.ToList();
        logs.Should().Contain(l => l.Contains("could not be extracted", StringComparison.Ordinal) &&
                                   l.Contains("System.Xml.XmlException", StringComparison.Ordinal),
            "the parse failure is logged with the exception types and stack");
        logs.Should().Contain(l => l.Contains("Unhandled exception", StringComparison.Ordinal) &&
                                   l.Contains("System.Xml.XmlException", StringComparison.Ordinal));
        var secrets = new[] { readSentinel, providerSentinel, u.Email };
        ShouldNotContainAny(logs, secrets, "the logs");
        await ShouldNotBePersistedAsync(secrets);
    }

    /// <summary>
    /// Admin calls are audited by route template, route id and outcome; never the query string or the body. (The board
    /// token is invalid, so nothing is fetched: a valid one is a public company slug and appears in the source URL that
    /// the HTTP client logs, by design.)
    /// </summary>
    [Fact]
    public async Task Admin_audit_rows_hold_routes_ids_and_outcomes_only()
    {
        await using var f = new TailorlyAppFactory(fx.ConnectionString, Verbose);
        const string sentinel = "SENTINEL-ADMIN-5518";
        var admin = await TestUsers.CreateAdminAsync(f, "sentinel-admin");

        (await admin.Api.GetAsync($"/api/v1/admin/companies?q={sentinel}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.Api.PostAsync("/api/v1/admin/companies",
                new { name = sentinel, websiteDomain = sentinel + ".example", platform = "greenhouse", boardToken = "not a token" }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        f.Http.Requests.Should().BeEmpty("an invalid board token is refused before any request");

        var mine = (await PersistedText.AuditLogAsync(fx.ConnectionString))
            .Where(r => r.Contains(admin.Id.ToString(), StringComparison.Ordinal)).ToList();
        mine.Should().HaveCount(2, "both calls are audited, the refused one too")
            .And.OnlyContain(r => r.Contains("/admin/companies", StringComparison.Ordinal));
        var secrets = new[] { sentinel, admin.Email };
        ShouldNotContainAny(f.Logs.Lines, secrets, "the logs");
        await ShouldNotBePersistedAsync(secrets);
    }
}
