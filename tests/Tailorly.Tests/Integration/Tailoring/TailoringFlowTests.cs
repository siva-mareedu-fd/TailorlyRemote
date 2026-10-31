using System.IO.Compression;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Ai;
using Tailorly.App.Features.Resumes;
using Tailorly.Docx;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.TailoringTestData;

namespace Tailorly.Tests.Integration.Tailoring;

/// <summary>
/// ATS analysis, tailoring and finalising (PRD F8, ApiDesign §4.8, SDD §7.4) through the API with the fake provider:
/// the async pattern, edit decisions with the new-fact rule, the format-preserving result, and every error code.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TailoringFlowTests : IAsyncLifetime
{
    private readonly PostgresAppFixture _fx;
    private TailorlyAppFactory F => _fx.Factory;
    private string Cs => _fx.ConnectionString;

    public TailoringFlowTests(PostgresAppFixture fx) => _fx = fx;

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
    public async Task Analyse_confirm_tailor_decide_finalize_and_download_keeps_the_format()
    {
        var (u, jobId, uj) = await TrackedJobAsync(F);

        // 1. Analysis of the original résumé.
        var analysis = await AnalyseAsync(F, u, uj);
        analysis.GetProperty("status").GetString().Should().Be("ready");
        var before = analysis.GetProperty("atsScore").GetInt32();
        before.Should().BeInRange(1, 99);
        var c = analysis.GetProperty("components");
        foreach (var name in new[] { "keywordCoverage", "requirementsMatch", "titleSeniority", "experienceRelevance", "formatHealth" })
            c.GetProperty(name).GetDouble().Should().BeInRange(0, 1, name);
        analysis.GetProperty("requirements").GetArrayLength().Should().Be(3);
        analysis.GetProperty("keywords").GetProperty("matched").EnumerateArray().Select(x => x.GetString()).Should().Contain("C#");
        analysis.GetProperty("disclaimer").GetString().Should().Contain("Estimated");
        var gap = analysis.GetProperty("gaps").EnumerateArray().Single(g => g.GetProperty("skill").GetString() == "Kubernetes");
        gap.GetProperty("likelyHas").GetBoolean().Should().BeTrue();
        gap.GetProperty("confirmed").GetBoolean().Should().BeFalse();
        (await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}"))).GetProperty("atsBefore").GetInt32().Should().Be(before);

        // 2. Proposed edits: one safe, one adding a skill the résumé lacks, one far too long.
        var doc = await TailorAsync(F, u, uj, Tailor(
            Edit("Built REST", "Built REST APIs in C# and PostgreSQL for the payments team."),
            Edit("Platforms:", "Platforms: .NET, PostgreSQL, Docker, Kubernetes"),
            Edit("Maintained billing", "Maintained billing services and wrote integration tests for every release, owning the design, the reviews, the " +
                                       "test plans and the release notes of the billing services.")));
        doc.GetProperty("status").GetString().Should().Be("proposed");
        var docId = doc.GetProperty("id").GetGuid();
        doc.GetProperty("atsBefore").GetInt32().Should().Be(before);
        doc.GetProperty("atsAfterBasis").GetString().Should().Be("projected");
        doc.GetProperty("unaddressedGaps").EnumerateArray().Select(x => x.GetString()).Should().Equal("Kubernetes");
        var safe = EditFor(doc, "Built REST");
        var newFact = EditFor(doc, "Platforms:");
        var longOne = EditFor(doc, "Maintained billing");
        safe.GetProperty("flags").GetArrayLength().Should().Be(0);
        safe.GetProperty("decision").GetString().Should().Be("pending");
        safe.GetProperty("diff").EnumerateArray().Should().Contain(d => d.GetProperty("type").GetString() == "insert");
        newFact.GetProperty("flags").EnumerateArray().Select(x => x.GetString()).Should().Contain("new_fact");
        newFact.GetProperty("newTerms").EnumerateArray().Select(x => x.GetString()).Should().Equal("Kubernetes");
        newFact.GetProperty("decision").GetString().Should().Be("rejected", "new facts are rejected by default");
        newFact.GetProperty("proposed").GetString().Should().StartWith("Platforms: ", "the locked label is kept");
        longOne.GetProperty("flags").EnumerateArray().Select(x => x.GetString()).Should().Contain("length");

        // 3. A new fact cannot be accepted until the user confirms the term.
        var refused = await DecideAsync(u, docId, (newFact.GetProperty("id").GetString()!, "accepted"));
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.TextAsync(refused)).Should().Contain("Kubernetes");
        (await u.Api.PostAsync("/api/v1/me/confirmed-skills", new { skill = "Kubernetes" })).StatusCode.Should().Be(HttpStatusCode.Created);
        var decided = await DecideAsync(u, docId, (newFact.GetProperty("id").GetString()!, "accepted"),
            (safe.GetProperty("id").GetString()!, "accepted"), (longOne.GetProperty("id").GetString()!, "rejected"));
        decided.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(decided));
        var afterDecisions = await ApiClient.JsonAsync(decided);
        afterDecisions.GetProperty("atsAfter").GetInt32().Should().BeGreaterThan(before, "Kubernetes now appears in the résumé text");
        (await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}/analysis")))
            .GetProperty("gaps").EnumerateArray().Single(g => g.GetProperty("skill").GetString() == "Kubernetes")
            .GetProperty("confirmed").GetBoolean().Should().BeTrue();

        // 4. Finalise: applies the two accepted edits and re-scores the final text.
        MatchingTestData.Reply(F, AiTaskNames.AtsAnalysis, Analysis(kubernetesStatus: "partial", kubernetesEvidence: "Platforms:"));
        var fin = await u.Api.PostAsync($"/api/v1/tailored-documents/{docId}/finalize");
        fin.StatusCode.Should().Be(HttpStatusCode.Accepted, await ApiClient.TextAsync(fin));
        (await ApiClient.JsonAsync(fin)).GetProperty("status").GetString().Should().Be("finalizing");
        await F.Jobs.RunAllAsync();
        var ready = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/tailored-documents/{docId}"));
        ready.GetProperty("status").GetString().Should().Be("ready");
        ready.GetProperty("atsAfterBasis").GetString().Should().Be("rescored");
        ready.GetProperty("atsAfter").GetInt32().Should().BeGreaterThan(before);
        ready.GetProperty("downloadable").GetBoolean().Should().BeTrue();
        ready.GetProperty("formats").EnumerateArray().Select(x => x.GetString()).Should().Equal("docx");
        ready.GetProperty("finalizedAt").GetString().Should().NotBeNull();

        // 5. Download: the two accepted paragraphs changed; every other part is byte for byte the original.
        var dl = await u.Api.GetAsync($"/api/v1/tailored-documents/{docId}/download");
        dl.StatusCode.Should().Be(HttpStatusCode.OK);
        dl.Content.Headers.ContentType!.MediaType.Should().Be(ResumeEndpoints.DocxContentType);
        dl.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        dl.Content.Headers.ContentDisposition.FileNameStar.Should().StartWith("Resume - Acme Cloud - ");
        dl.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        var bytes = await dl.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var text = ResumeExtractor.Extract(new MemoryStream(bytes)).Paragraphs.Select(p => p.Text).ToList();
        text.Should().Contain("Built REST APIs in C# and PostgreSQL for the payments team.");
        text.Should().Contain("Platforms: .NET, PostgreSQL, Docker, Kubernetes");
        text.Should().Contain("Maintained billing services and wrote integration tests for every release.", "a rejected edit is not applied");
        var originalKey = await Db.ScalarAsync<string>(Cs, "SELECT r.storage_key FROM tailored_documents d JOIN resumes r ON r.id = d.resume_id WHERE d.id = @id",
            ("id", docId));
        PartsOtherThanDocument(bytes).Should().BeEquivalentTo(PartsOtherThanDocument(File.ReadAllBytes(Path.Combine(F.StorageRoot, originalKey!))),
            "styles, numbering, settings and relationships are copied from the uploaded original byte for byte");

        // 6. The tracker entry moved Saved → Tailored and records before → after.
        var entry = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}"));
        entry.GetProperty("status").GetString().Should().Be("tailored");
        entry.GetProperty("atsAfter").GetInt32().Should().Be(ready.GetProperty("atsAfter").GetInt32());
        entry.GetProperty("statusHistory").EnumerateArray().Select(h => h.GetProperty("status").GetString()).Should().Equal("saved", "tailored");
        entry.GetProperty("documents").GetProperty("resumeStatus").GetString().Should().Be("ready");
        (await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}/tailored-documents")))
            .EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).Should().Contain(docId);
        (await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/jobs/{jobId}"))).GetProperty("tracked").GetProperty("userJobId").GetGuid()
            .Should().Be(uj);
    }

    [Fact]
    public async Task Requests_while_work_is_in_flight_return_the_same_resource_with_200()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "inflight");
        var first = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/analysis", new { });
        var second = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/analysis", new { });
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiClient.JsonAsync(second)).GetProperty("id").GetGuid().Should().Be((await ApiClient.JsonAsync(first)).GetProperty("id").GetGuid());
        MatchingTestData.Reply(F, AiTaskNames.AtsAnalysis, Analysis());
        await F.Jobs.RunAllAsync();

        var t1 = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/tailored-resumes", new { });
        var t2 = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/tailored-resumes", new { });
        t1.StatusCode.Should().Be(HttpStatusCode.Accepted);
        t2.StatusCode.Should().Be(HttpStatusCode.OK);
        var c1 = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { tone = "concise" });
        var c2 = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { tone = "confident" });
        c1.StatusCode.Should().Be(HttpStatusCode.Accepted);
        c2.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiClient.JsonAsync(c2)).GetProperty("id").GetGuid().Should().Be((await ApiClient.JsonAsync(c1)).GetProperty("id").GetGuid());
        MatchingTestData.Reply(F, AiTaskNames.ResumeTailor, Tailor(Edit("Built REST", "Built REST APIs in C# and PostgreSQL for the payments team.")));
        MatchingTestData.Reply(F, AiTaskNames.CoverLetter, Letter());
        await F.Jobs.RunAllAsync();

        var docId = (await ApiClient.JsonAsync(t1)).GetProperty("id").GetGuid();
        var doc = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/tailored-documents/{docId}"));
        await DecideAsync(u, docId, (Edits(doc).Single().GetProperty("id").GetString()!, "accepted"));
        (await u.Api.PostAsync($"/api/v1/tailored-documents/{docId}/finalize")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var again = await u.Api.PostAsync($"/api/v1/tailored-documents/{docId}/finalize");
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiClient.JsonAsync(again)).GetProperty("status").GetString().Should().Be("finalizing");
        await F.Jobs.RunAllAsync();
    }

    [Fact]
    public async Task Every_ai_start_refuses_with_BUDGET_EXCEEDED_when_documents_is_used_up()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "budget");
        await AnalyseAsync(F, u, uj);
        var doc = await TailorAsync(F, u, uj, Tailor(Edit("Built REST", "Built REST APIs in C# and PostgreSQL for the payments team.")));
        var docId = doc.GetProperty("id").GetGuid();
        await DecideAsync(u, docId, (Edits(doc).Single().GetProperty("id").GetString()!, "accepted"));

        await Db.SetUsedAsync(Cs, u.Id, "documents", 1_400_000, 1_400_000);
        foreach (var (path, body) in new (string, object?)[]
                 {
                     ($"/api/v1/user-jobs/{uj}/analysis", new { }), ($"/api/v1/user-jobs/{uj}/tailored-resumes", new { }),
                     ($"/api/v1/user-jobs/{uj}/cover-letters", new { tone = "concise" }), ($"/api/v1/tailored-documents/{docId}/finalize", null),
                 })
        {
            var res = await u.Api.PostAsync(path, body);
            res.StatusCode.Should().Be(HttpStatusCode.Conflict, path);
            var problem = await ApiClient.JsonAsync(res);
            problem.GetProperty("code").GetString().Should().Be("BUDGET_EXCEEDED", path);
            problem.GetProperty("extra").GetProperty("bucket").GetString().Should().Be("documents");
            problem.GetProperty("extra").GetProperty("resetsOn").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-01$");
        }
        await Db.SetUsedAsync(Cs, u.Id, "documents", 0, 1_400_000);
    }

    [Fact]
    public async Task A_limited_listing_needs_a_pasted_description_which_is_enriched_on_the_users_budget()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "paste", description: "Short teaser for a backend role. Apply on our site.");
        var refused = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/analysis", new { });
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await ApiClient.JsonAsync(refused);
        problem.GetProperty("code").GetString().Should().Be("JOB_DESCRIPTION_REQUIRED");
        problem.GetProperty("extra").GetProperty("action").GetString().Should().Be("paste_description");
        (await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        (await u.Api.PutAsync($"/api/v1/user-jobs/{uj}/description", new { description = "" })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
        (await u.Api.PutAsync($"/api/v1/user-jobs/{uj}/description", new { description = new string('x', 30_001) })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
        var pasted = await u.Api.PutAsync($"/api/v1/user-jobs/{uj}/description", new { description = MatchingTestData.Description });
        pasted.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = await ApiClient.JsonAsync(pasted);
        entry.GetProperty("hasUserDescription").GetBoolean().Should().BeTrue();
        entry.GetProperty("enrichmentStatus").GetString().Should().Be("queued");

        MatchingTestData.Reply(F, AiTaskNames.ByoJobEnrichment, MatchingTestData.Enrichment());
        var analysis = await AnalyseAsync(F, u, uj);
        analysis.GetProperty("status").GetString().Should().Be("ready", analysis.ToString());
        (await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}"))).GetProperty("enrichmentStatus").GetString().Should().Be("ready");
        F.Ai.Requests.Should().Contain(r => r.Task == AiTaskNames.ByoJobEnrichment && r.Messages[0].Text.Contains("payment infrastructure"));
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM ai_usage WHERE user_id = @u AND task = 'byo_job_enrichment' AND bucket = 'documents'",
            ("u", u.Id))).Should().Be(1, "pasted text is enriched on the user's Documents budget");
    }

    [Fact]
    public async Task Tailoring_needs_a_ready_analysis_and_finalising_needs_an_accepted_unchanged_edit()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "preconditions");
        var noAnalysis = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/tailored-resumes", new { });
        noAnalysis.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.CodeAsync(noAnalysis)).Should().Be("ANALYSIS_REQUIRED");

        await AnalyseAsync(F, u, uj);
        var doc = await TailorAsync(F, u, uj, Tailor(Edit("Built REST", "Built REST APIs in C# and PostgreSQL for the payments team.")));
        var docId = doc.GetProperty("id").GetGuid();
        var none = await u.Api.PostAsync($"/api/v1/tailored-documents/{docId}/finalize");
        none.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.CodeAsync(none)).Should().Be("NO_ACCEPTED_EDITS");

        await DecideAsync(u, docId, (Edits(doc).Single().GetProperty("id").GetString()!, "accepted"));
        await Db.ExecAsync(Cs, """
            UPDATE tailored_documents
            SET edits = jsonb_set(edits, '{items,0,fingerprint}', '"00000000"')
            WHERE id = @id
            """, ("id", docId));
        var stale = await u.Api.PostAsync($"/api/v1/tailored-documents/{docId}/finalize");
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.CodeAsync(stale)).Should().Be("STALE_EDIT");

        // Decisions only while proposed; unknown edits and decisions are validation errors.
        (await DecideAsync(u, docId, ("e99", "accepted"))).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await DecideAsync(u, docId, (Edits(doc).Single().GetProperty("id").GetString()!, "maybe"))).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_model_that_invents_a_number_gets_one_repair_and_then_the_tailoring_fails_cleanly()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "invents");
        await AnalyseAsync(F, u, uj);

        var invented = Tailor(Edit("Built REST", "Built REST APIs serving 4 million requests a day for the payments team."));
        MatchingTestData.Reply(F, AiTaskNames.ResumeTailor, invented);
        var failed = await TailorAsync(F, u, uj, invented);
        failed.GetProperty("status").GetString().Should().Be("failed");
        failed.GetProperty("errorCode").GetString().Should().Be("AI_INVALID_OUTPUT");
        var requests = F.Ai.Requests.Where(r => r.Task == AiTaskNames.ResumeTailor).TakeLast(2).ToList();
        requests[1].Messages.Last().Text.Should().Contain("adds a number that does not appear in the résumé");

        // The repaired reply is used.
        MatchingTestData.Reply(F, AiTaskNames.ResumeTailor, invented);
        var repaired = await TailorAsync(F, u, uj, Tailor(Edit("Built REST", "Built REST APIs in C# and PostgreSQL for the payments team.")));
        repaired.GetProperty("status").GetString().Should().Be("proposed");
        Edits(repaired).Single().GetProperty("proposed").GetString().Should().NotContain("4 million");
    }

    [Fact]
    public async Task The_tailoring_prompt_carries_only_delimited_data_and_the_contract_inputs()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "prompt");
        await AnalyseAsync(F, u, uj);
        await TailorAsync(F, u, uj, Tailor(Edit("Built REST", "Built REST APIs in C# and PostgreSQL for the payments team.")));
        var r = F.Ai.Requests.Last(x => x.Task == AiTaskNames.ResumeTailor);
        r.CacheablePrefix.Should().StartWith("<resume_data>").And.Contain($"{Pid("Languages:")} | skills | editable | prefix \"Languages:\" | C#, SQL, TypeScript")
            .And.Contain($"{Pid("Senior Software Engineer")} | experience | locked |");
        var data = r.Messages[0].Text;
        foreach (var tag in new[] { "job_data", "analysis_data", "confirmed_skills_data", "limits_data" })
            data.Should().Contain($"<{tag}>").And.Contain($"</{tag}>");
        data.Should().Contain("Maximum number of edits: 25").And.Contain("Kubernetes");
        r.JsonSchema.Should().Contain("paragraph_id", "the reply is constrained to the edit-only schema");
    }

    [Fact]
    public async Task Cover_letter_in_a_tone_with_notes_is_written_edited_and_downloaded()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "letter");
        (await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { tone = "formal" })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { notes = new string('n', 501) })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);

        // A first reply with a cliché is repaired.
        MatchingTestData.Reply(F, AiTaskNames.CoverLetter, Letter("confident", "Hello Acme Cloud team,", "I am a team player who builds .NET services.",
            "Best regards,", "Asha Verma"));
        MatchingTestData.Reply(F, AiTaskNames.CoverLetter, Letter("confident", "Hello Acme Cloud team,",
            "Priya suggested I apply: I build .NET and PostgreSQL services for payments teams.",
            "At Example Corp I built REST APIs for the payments team using C# and PostgreSQL.", "Best regards,", "Asha Verma"));
        var start = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { tone = "confident", notes = "Referred by Priya" });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted, await ApiClient.TextAsync(start));
        var id = (await ApiClient.JsonAsync(start)).GetProperty("id").GetGuid();
        await F.Jobs.RunAllAsync();

        var letter = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/tailored-documents/{id}"));
        letter.GetProperty("status").GetString().Should().Be("ready", letter.ToString());
        letter.GetProperty("kind").GetString().Should().Be("cover_letter");
        letter.GetProperty("tone").GetString().Should().Be("confident");
        var content = letter.GetProperty("content");
        content.GetProperty("notes").GetString().Should().Be("Referred by Priya");
        content.GetProperty("paragraphs").EnumerateArray().Select(p => p.GetString()).Should().Contain(p => p!.Contains("Priya"));
        content.GetProperty("wordCount").GetInt32().Should().BeInRange(20, 250);
        F.Ai.Requests.Last(r => r.Task == AiTaskNames.CoverLetter).Messages.Last().Text.Should().Contain("Avoid the phrase \"team player\"");
        F.Ai.Requests.Last(r => r.Task == AiTaskNames.CoverLetter).Messages[0].Text.Should().Contain("<notes_data>\nReferred by Priya\n</notes_data>");
        (await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}"))).GetProperty("status").GetString().Should().Be("tailored");

        var edited = await u.Api.PutAsync($"/api/v1/tailored-documents/{id}/content", new { paragraphs = new[] { "Hello Acme Cloud team,", "My own words.", "Asha" } });
        edited.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiClient.JsonAsync(edited)).GetProperty("content").GetProperty("wordCount").GetInt32().Should().Be(8);
        var dl = await u.Api.GetAsync($"/api/v1/tailored-documents/{id}/download");
        dl.StatusCode.Should().Be(HttpStatusCode.OK);
        dl.Content.Headers.ContentDisposition!.FileNameStar.Should().StartWith("Cover letter - Acme Cloud");
        using (var d = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(
                   new MemoryStream(await dl.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)), false))
            d.MainDocumentPart!.Document!.Body!.InnerText.Should().Contain("My own words.");
        (await u.Api.PutAsync($"/api/v1/tailored-documents/{id}/content", new { paragraphs = Enumerable.Repeat("x", 13) })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_letter_over_the_word_limit_is_sent_back_once()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "letter-long");
        var longParagraph = string.Join(" ", Enumerable.Repeat("I build .NET services for payments teams at Example Corp.", 8));
        MatchingTestData.Reply(F, AiTaskNames.CoverLetter, Letter("concise", "Hello Acme Cloud team,", longParagraph, longParagraph, longParagraph, "Asha Verma"));
        MatchingTestData.Reply(F, AiTaskNames.CoverLetter, Letter());
        var start = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { });
        await F.Jobs.RunAllAsync();
        var id = (await ApiClient.JsonAsync(start)).GetProperty("id").GetGuid();
        (await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/tailored-documents/{id}"))).GetProperty("tone").GetString().Should().Be("concise");
        F.Ai.Requests.Last(r => r.Task == AiTaskNames.CoverLetter).Messages.Last().Text.Should().Contain("the limit for a concise letter is 180");
    }

    [Fact]
    public async Task Pdf_downloads_only_when_the_flag_is_on()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "pdf");
        MatchingTestData.Reply(F, AiTaskNames.CoverLetter, Letter());
        var start = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { });
        var id = (await ApiClient.JsonAsync(start)).GetProperty("id").GetGuid();
        await F.Jobs.RunAllAsync();

        var off = await u.Api.GetAsync($"/api/v1/tailored-documents/{id}/download?format=pdf");
        off.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ApiClient.CodeAsync(off)).Should().Be("FORMAT_UNAVAILABLE");
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/meta"))).GetProperty("pdfExport").GetBoolean().Should().BeFalse();

        await Db.ExecAsync(Cs, "UPDATE feature_flags SET value = 'true' WHERE key = 'pdf_export.enabled'");
        try
        {
            F.Http.Always("http://gotenberg:3000/forms/libreoffice/convert", (_, _) =>
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("%PDF-1.7 fake"u8.ToArray()) };
                res.Content.Headers.ContentType = new("application/pdf");
                return Task.FromResult(res);
            });
            (await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/tailored-documents/{id}"))).GetProperty("formats")
                .EnumerateArray().Select(x => x.GetString()).Should().Equal("docx", "pdf");
            var on = await u.Api.GetAsync($"/api/v1/tailored-documents/{id}/download?format=pdf");
            on.StatusCode.Should().Be(HttpStatusCode.OK);
            on.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
            on.Content.Headers.ContentDisposition!.FileNameStar.Should().EndWith(".pdf");

            F.Http.Always("http://gotenberg:3000/forms/libreoffice/convert", 500, "down");
            var down = await u.Api.GetAsync($"/api/v1/tailored-documents/{id}/download?format=pdf");
            down.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            await Db.ExecAsync(Cs, "UPDATE feature_flags SET value = 'false' WHERE key = 'pdf_export.enabled'");
        }
        (await u.Api.GetAsync($"/api/v1/tailored-documents/{id}/download?format=odt")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Deleting_a_document_or_the_resume_removes_files_and_rows()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "delete");
        MatchingTestData.Reply(F, AiTaskNames.CoverLetter, Letter());
        var start = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { });
        var id = (await ApiClient.JsonAsync(start)).GetProperty("id").GetGuid();
        await F.Jobs.RunAllAsync();
        var key = await Db.ScalarAsync<string>(Cs, "SELECT storage_key FROM tailored_documents WHERE id = @id", ("id", id));
        File.Exists(Path.Combine(F.StorageRoot, key!)).Should().BeTrue();
        (await u.Api.DeleteAsync($"/api/v1/tailored-documents/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(F.StorageRoot, key!)).Should().BeFalse();
        (await u.Api.GetAsync($"/api/v1/tailored-documents/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        MatchingTestData.Reply(F, AiTaskNames.CoverLetter, Letter());
        var again = await ApiClient.JsonAsync(await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { }));
        await F.Jobs.RunAllAsync();
        var key2 = await Db.ScalarAsync<string>(Cs, "SELECT storage_key FROM tailored_documents WHERE id = @id", ("id", again.GetProperty("id").GetGuid()));
        var resumeId = (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/resumes"))).EnumerateArray()
            .Single(r => r.GetProperty("isActive").GetBoolean()).GetProperty("id").GetGuid();
        (await u.Api.DeleteAsync($"/api/v1/resumes/{resumeId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(F.StorageRoot, key2!)).Should().BeFalse("tailored files live under the résumé's prefix");
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM tailored_documents WHERE resume_id = @r", ("r", resumeId))).Should().Be(0);
    }

    [Fact]
    public async Task Stuck_analyses_and_documents_are_failed_with_TIMEOUT()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "sweep");
        var start = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/analysis", new { });
        var id = (await ApiClient.JsonAsync(start)).GetProperty("id").GetGuid();
        F.Jobs.Clear();
        await Db.ExecAsync(Cs, "UPDATE ats_analyses SET updated_at = now() - interval '31 minutes' WHERE id = @id", ("id", id));
        await using (var scope = F.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<SweepStuckWorkJob>().RunAsync(TestContext.Current.CancellationToken);
        var a = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}/analysis"));
        a.GetProperty("status").GetString().Should().Be("failed");
        a.GetProperty("errorCode").GetString().Should().Be("TIMEOUT");
    }

    [Fact]
    public async Task Tracking_is_idempotent_needs_a_job_scored_for_the_caller_and_shows_on_the_shortlist()
    {
        var (u, jobId, uj) = await TrackedJobAsync(F, "track");
        var again = await u.Api.PostAsync("/api/v1/user-jobs", new { jobId });
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiClient.JsonAsync(again)).GetProperty("id").GetGuid().Should().Be(uj);
        var entry = await ApiClient.JsonAsync(again);
        entry.GetProperty("title").GetString().Should().StartWith("Senior ");
        entry.GetProperty("companyName").GetString().Should().Be("Acme Cloud");
        entry.GetProperty("fitScore").GetInt32().Should().Be(9);
        entry.GetProperty("source").GetProperty("key").GetString().Should().Be("greenhouse");
        entry.GetProperty("descriptionAvailable").GetBoolean().Should().BeTrue();
        entry.GetProperty("status").GetString().Should().Be("saved");

        (await u.Api.PostAsync("/api/v1/user-jobs", new { jobId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await u.Api.PostAsync("/api/v1/user-jobs", new { })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var unscored = await MatchingTestData.InsertJobAsync(F, "Someone else's job");
        (await u.Api.PostAsync("/api/v1/user-jobs", new { jobId = unscored })).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "jobs not scored for the caller are not reachable by id");

        var saved = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/jobs?savedOnly=true&minScore=1"));
        MatchingTestData.JobIds(saved).Should().Equal(jobId);
        saved.GetProperty("items")[0].GetProperty("tracked").GetProperty("status").GetString().Should().Be("saved");
    }

    private static Dictionary<string, string> PartsOtherThanDocument(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        return zip.Entries.Where(e => e.FullName != "word/document.xml").ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ms.ToArray()));
        });
    }
}
