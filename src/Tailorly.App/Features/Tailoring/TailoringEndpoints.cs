using Hangfire;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Tailorly.App.Api;
using Tailorly.App.Features.Resumes;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Ports;
using Tailorly.Docx;
using Tailorly.Docx.Tailoring;

namespace Tailorly.App.Features.Tailoring;

/// <summary>
/// ATS analysis, tailored résumés and cover letters (ApiDesign §4.8). Starting work follows the async pattern (§3):
/// preconditions, then <c>202</c> with the resource the job fills, or <c>200</c> with the one already in flight (partial
/// unique indexes make a duplicate impossible). Every id resolves through the user-scoped context first, so another user's
/// resource is a 404 before anything else is checked.
/// </summary>
public static partial class TailoringEndpoints
{
    public const int MaxNotesChars = 500;
    public const int MaxLetterParagraphs = 12;
    public const int MaxLetterChars = 3_000;

    public static void MapTailoringEndpoints(this RouteGroupBuilder api)
    {
        var jobs = api.MapGroup("/user-jobs/{id:guid}").WithTags("Tailoring").RequireAuthorization(AuthPolicies.User);
        jobs.MapPost("/analysis", StartAnalysis).WithName("StartAnalysis").RequireRateLimiting(RateLimiting.AiStart);
        jobs.MapGet("/analysis", GetAnalysis).WithName("GetAnalysis");
        jobs.MapPost("/tailored-resumes", StartTailoring).WithName("StartTailoredResume").RequireRateLimiting(RateLimiting.AiStart);
        jobs.MapPost("/cover-letters", StartCoverLetter).WithName("StartCoverLetter").RequireRateLimiting(RateLimiting.AiStart);

        var docs = api.MapGroup("/tailored-documents").WithTags("Tailoring").RequireAuthorization(AuthPolicies.User);
        docs.MapGet("/{id:guid}", GetDocument).WithName("GetTailoredDocument");
        docs.MapPost("/{id:guid}/decisions", Decide).WithName("DecideEdits");
        docs.MapPost("/{id:guid}/finalize", FinalizeDocument).WithName("FinalizeTailoredResume").RequireRateLimiting(RateLimiting.AiStart);
        docs.MapGet("/{id:guid}/download", Download).WithName("DownloadTailoredDocument").RequireRateLimiting(RateLimiting.Download)
            .Produces(StatusCodes.Status200OK, contentType: ResumeEndpoints.DocxContentType)
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf");
        docs.MapDelete("/{id:guid}", DeleteDocument).WithName("DeleteTailoredDocument");
        docs.MapPut("/{id:guid}/content", PutContent).WithName("PutCoverLetterContent");
    }

    private static ApiException DocumentNotFound() => TailoringData.NotFound("This document does not exist.");

    private static ApiException Conflict(string detail) =>
        new(StatusCodes.Status409Conflict, ErrorCodes.ConcurrentUpdate, "Changed meanwhile", detail);

    // ---------- analysis ----------

    /// <summary>
    /// ATS analysis of the original résumé (default: the active one) against the tracked job. Re-posting after it finished
    /// runs it again (charged) and replaces the result; while it runs, the request returns it with 200.
    /// </summary>
    private static async Task<Results<Accepted<AnalysisDto>, Ok<AnalysisDto>>> StartAnalysis(Guid id, AnalysisRequest? body,
        AppDbContext db, TailoringData data, ICurrentUser current, IBackgroundJobClient jobs, TimeProvider clock, CancellationToken ct)
    {
        var uj = await data.UserJobAsync(id, ct);
        await data.RequireConsentAsync(ct);
        var resume = await data.ResumeAsync(body?.ResumeId, ct);
        await data.RequireDescriptionAsync(uj, ct);

        var confirmed = await ConfirmedKeysAsync(db, ct);
        var row = await db.AtsAnalyses.FirstOrDefaultAsync(a => a.UserJobId == uj.Id && a.ResumeId == resume.Id, ct);
        if (row is { Status: WorkStatus.Queued or WorkStatus.Running }) return TypedResults.Ok(AtsScoring.ToDto(row, confirmed));
        await data.RequireBudgetAsync(current.UserId, ct);

        var now = clock.GetUtcNow();
        if (row is null)
        {
            row = new AtsAnalysis { UserJobId = uj.Id, UserId = current.UserId, ResumeId = resume.Id, CreatedAt = now };
            db.AtsAnalyses.Add(row);
        }
        row.Status = WorkStatus.Queued;
        row.ErrorCode = null;
        row.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(row).State = EntityState.Detached;
            var raced = await db.AtsAnalyses.AsNoTracking().FirstAsync(a => a.UserJobId == uj.Id && a.ResumeId == resume.Id, ct);
            return TypedResults.Ok(AtsScoring.ToDto(raced, confirmed));
        }
        var analysisId = row.Id;
        var userId = current.UserId;
        jobs.Enqueue<AtsAnalysisJob>(j => j.RunAsync(analysisId, userId, null, CancellationToken.None));
        return TypedResults.Accepted($"/api/v1/user-jobs/{uj.Id}/analysis", AtsScoring.ToDto(row, confirmed));
    }

    /// <summary>The latest analysis of the tracked job (any résumé version). Poll here.</summary>
    private static async Task<Ok<AnalysisDto>> GetAnalysis(Guid id, AppDbContext db, TailoringData data, CancellationToken ct)
    {
        await data.UserJobAsync(id, ct);
        var row = await db.AtsAnalyses.AsNoTracking().Where(a => a.UserJobId == id).OrderByDescending(a => a.UpdatedAt).FirstOrDefaultAsync(ct)
                  ?? throw TailoringData.NotFound("This job has not been analysed yet.");
        return TypedResults.Ok(AtsScoring.ToDto(row, await ConfirmedKeysAsync(db, ct)));
    }

    private static async Task<HashSet<string>> ConfirmedKeysAsync(AppDbContext db, CancellationToken ct) =>
        (await db.UserConfirmedSkills.AsNoTracking().Select(s => s.SkillKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    // ---------- tailored résumé ----------

    /// <summary>
    /// Proposes edits for the résumé (default: the active one); needs a ready analysis for that résumé
    /// (<c>ANALYSIS_REQUIRED</c>). Ends at <c>proposed</c>. A tailoring already in flight is returned with 200.
    /// </summary>
    private static async Task<Results<Accepted<TailoredDocumentDto>, Ok<TailoredDocumentDto>>> StartTailoring(Guid id, TailorRequest? body,
        AppDbContext db, TailoringData data, TailoredDocuments documents, ICurrentUser current, IBackgroundJobClient jobs, TimeProvider clock,
        CancellationToken ct)
    {
        var uj = await data.UserJobAsync(id, ct);
        await data.RequireConsentAsync(ct);
        var resume = await data.ResumeAsync(body?.ResumeId, ct);
        await data.RequireDescriptionAsync(uj, ct);
        if (!await db.AtsAnalyses.AnyAsync(a => a.UserJobId == uj.Id && a.ResumeId == resume.Id && a.Status == WorkStatus.Ready, ct))
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, ErrorCodes.AnalysisRequired, "Analysis required",
                "Run the ATS analysis for this résumé first.");
        return await StartDocumentAsync(db, documents, current, jobs, clock, uj, resume, DocumentKinds.Resume, null, null, data, ct);
    }

    // ---------- cover letter ----------

    /// <summary>A cover letter in the chosen tone (default concise), with optional notes; does not need an analysis.</summary>
    private static async Task<Results<Accepted<TailoredDocumentDto>, Ok<TailoredDocumentDto>>> StartCoverLetter(Guid id,
        CoverLetterRequest? body, AppDbContext db, TailoringData data, TailoredDocuments documents, ICurrentUser current,
        IBackgroundJobClient jobs, IOptions<CoverLetterOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var uj = await data.UserJobAsync(id, ct);
        var errors = new FieldErrors();
        var tone = string.IsNullOrWhiteSpace(body?.Tone) ? CoverLetterOptions.DefaultTone : body.Tone.Trim();
        if (!options.Value.EffectiveTones.ContainsKey(tone))
            errors.Add("tone", $"Choose one of: {string.Join(", ", options.Value.EffectiveTones.Keys)}.");
        var notes = errors.Text("notes", body?.Notes, MaxNotesChars, required: false, label: "Notes");
        errors.ThrowIfAny();

        await data.RequireConsentAsync(ct);
        var resume = await data.ResumeAsync(body?.ResumeId, ct);
        await data.RequireDescriptionAsync(uj, ct);
        return await StartDocumentAsync(db, documents, current, jobs, clock, uj, resume, DocumentKinds.CoverLetter, tone, notes, data, ct);
    }

    private static async Task<Results<Accepted<TailoredDocumentDto>, Ok<TailoredDocumentDto>>> StartDocumentAsync(AppDbContext db,
        TailoredDocuments documents, ICurrentUser current, IBackgroundJobClient jobs, TimeProvider clock, UserJob uj, Resume resume,
        string kind, string? tone, string? notes, TailoringData data, CancellationToken ct)
    {
        if (await InFlightAsync(db, uj.Id, kind, ct) is { } inflight) return TypedResults.Ok(await documents.ToDtoAsync(inflight, ct));
        await data.RequireBudgetAsync(current.UserId, ct);

        var now = clock.GetUtcNow();
        var doc = new TailoredDocument
        {
            UserJobId = uj.Id,
            UserId = current.UserId,
            ResumeId = resume.Id,
            Kind = kind,
            SourceResumeVersion = resume.Version,
            Tone = tone,
            // Notes stay in the row, never in the job queue (job arguments are ids only).
            Content = kind == DocumentKinds.CoverLetter ? TailoringJson.Write(new StoredLetter([], 0, notes)) : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.TailoredDocuments.Add(doc);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(doc).State = EntityState.Detached;
            if (await InFlightAsync(db, uj.Id, kind, ct) is { } raced) return TypedResults.Ok(await documents.ToDtoAsync(raced, ct));
            throw;
        }

        var docId = doc.Id;
        var userId = current.UserId;
        if (kind == DocumentKinds.Resume) jobs.Enqueue<TailorResumeJob>(j => j.RunAsync(docId, userId, null, CancellationToken.None));
        else jobs.Enqueue<CoverLetterJob>(j => j.RunAsync(docId, userId, null, CancellationToken.None));
        return TypedResults.Accepted($"/api/v1/tailored-documents/{doc.Id}", await documents.ToDtoAsync(doc, ct));
    }

    private static Task<TailoredDocument?> InFlightAsync(AppDbContext db, Guid userJobId, string kind, CancellationToken ct) =>
        db.TailoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.UserJobId == userJobId && d.Kind == kind
            && (d.Status == TailoredStatus.Queued || d.Status == TailoredStatus.Running || d.Status == TailoredStatus.Finalizing), ct);

    // ---------- documents ----------

    private static async Task<Ok<TailoredDocumentDto>> GetDocument(Guid id, AppDbContext db, TailoredDocuments documents, CancellationToken ct) =>
        TypedResults.Ok(await documents.ToDtoAsync(await db.TailoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw DocumentNotFound(), ct));

    /// <summary>
    /// Accept, reject or reset proposed edits (only while <c>proposed</c>). An edit flagged <c>new_fact</c> can be accepted
    /// only once every term it adds is among the user's confirmed skills (the hard rule: nothing enters the résumé that
    /// the user has not stated). The projected score is recomputed from the accepted edits.
    /// </summary>
    private static async Task<Ok<TailoredDocumentDto>> Decide(Guid id, DecisionsRequest body, AppDbContext db, TailoringData data,
        TailoredDocuments documents, AtsScoring scoring, TimeProvider clock, CancellationToken ct)
    {
        var doc = await db.TailoredDocuments.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw DocumentNotFound();
        if (doc.Kind != DocumentKinds.Resume) throw FieldErrors.Single("decisions", "Only a tailored résumé has edits to decide.");
        if (doc.Status != TailoredStatus.Proposed) throw FieldErrors.Single("decisions", "Changes can be decided only while they are proposed.");
        var edits = TailoringJson.Read<StoredEdits>(doc.Edits) ?? throw new InvalidDataException("The edits are missing.");

        var confirmed = await ConfirmedKeysAsync(db, ct);
        var errors = new FieldErrors();
        var updated = edits.Items.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var items = body.Decisions ?? [];
        if (items.Count == 0) errors.Add("decisions", "Send at least one decision.");
        for (var i = 0; i < items.Count; i++)
        {
            var (editId, decision) = (items[i].EditId ?? "", items[i].Decision ?? "");
            if (!updated.TryGetValue(editId, out var edit))
                errors.Add($"decisions[{i}].editId", "No such edit.");
            else if (decision is not (EditDecisions.Accepted or EditDecisions.Rejected or EditDecisions.Pending))
                errors.Add($"decisions[{i}].decision", "Use accepted, rejected or pending.");
            else if (decision == EditDecisions.Accepted && Unconfirmed(edit, confirmed) is { Count: > 0 } terms)
                errors.Add($"decisions[{i}].decision", $"Confirm {string.Join(", ", terms)} in your skills before accepting this change.");
            else
                updated[editId] = edit with { Decision = decision };
        }
        errors.ThrowIfAny();

        var next = edits with { Items = edits.Items.Select(e => updated[e.Id]).ToList() };
        doc.Edits = TailoringJson.Write(next);
        await ProjectAsync(db, data, scoring, doc, next, ct);
        doc.UpdatedAt = clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Conflict("These changes were updated in another tab. Reload and try again.");
        }
        return TypedResults.Ok(await documents.ToDtoAsync(doc, ct));
    }

    private static IReadOnlyList<string> Unconfirmed(StoredEdit e, IReadOnlySet<string> confirmedKeys) =>
        e.NewTerms.Where(t => !confirmedKeys.Contains(UserConfirmedSkill.KeyOf(t))).ToList();

    /// <summary>The projected "after" score: keyword coverage with the accepted edits in place, the rest as analysed.</summary>
    private static async Task ProjectAsync(AppDbContext db, TailoringData data, AtsScoring scoring, TailoredDocument doc, StoredEdits edits,
        CancellationToken ct)
    {
        var analysis = await db.AtsAnalyses.AsNoTracking()
            .Where(a => a.UserJobId == doc.UserJobId && a.ResumeId == doc.ResumeId && a.Status == WorkStatus.Ready)
            .Select(a => a.Components).FirstOrDefaultAsync(ct);
        if (TailoringJson.Read<StoredScore>(analysis) is not { } before) return;
        var resume = await db.Resumes.AsNoTracking().FirstAsync(r => r.Id == doc.ResumeId, ct);
        var model = await TailoringData.ExtractAsync(await data.FileAsync(resume.StorageKey, ct), ct);
        var uj = await db.UserJobs.AsNoTracking().FirstAsync(u => u.Id == doc.UserJobId, ct);
        var enrichment = await StoredEnrichmentAsync(db, uj, ct);
        var replaced = edits.Items.Where(e => e.Decision == EditDecisions.Accepted).ToDictionary(e => e.ParagraphId, e => e.NewText);
        var (score, keywords) = scoring.Project(model, replaced, enrichment, before.Components);
        doc.AtsAfter = (short)score.Score;
        doc.AtsBreakdown = TailoringJson.Write(new StoredScore(AtsComponentsDto.From(score.Components),
            new KeywordsDto(keywords.Matched, keywords.Missing), AtsBasis.Projected));
    }

    /// <summary>The enrichment already stored for the tracked job (pasted text or listing), without calling the model.</summary>
    private static async Task<Ai.Tasks.EnrichmentOutput?> StoredEnrichmentAsync(AppDbContext db, UserJob uj, CancellationToken ct)
    {
        var json = uj.UserDescription is not null
            ? uj.UserEnrichment
            : uj.JobId is { } jobId ? await db.JobEnrichment.AsNoTracking().Where(e => e.JobId == jobId).Select(e => e.Data).FirstOrDefaultAsync(ct) : null;
        return Tailorly.App.Features.Jobs.ScoredJobs.ParseEnrichment(json)?.Enrichment;
    }

    /// <summary>
    /// Applies the accepted edits to a copy of the original and re-scores (pending edits count as rejected). Checked here
    /// so the user hears at once: no accepted edit (<c>NO_ACCEPTED_EDITS</c>), the résumé no longer matching the proposal
    /// (<c>STALE_EDIT</c>), an accepted new fact whose term was un-confirmed meanwhile. A finalising or finished document
    /// is returned with 200.
    /// </summary>
    private static async Task<Results<Accepted<TailoredDocumentDto>, Ok<TailoredDocumentDto>>> FinalizeDocument(Guid id, AppDbContext db,
        TailoringData data, TailoredDocuments documents, ICurrentUser current, IBackgroundJobClient jobs, IOptions<TailorOptions> options,
        TimeProvider clock, CancellationToken ct)
    {
        var doc = await db.TailoredDocuments.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw DocumentNotFound();
        if (doc.Kind != DocumentKinds.Resume) throw FieldErrors.Single("id", "Only a tailored résumé is finalised.");
        if (doc.Status is TailoredStatus.Finalizing or TailoredStatus.Ready) return TypedResults.Ok(await documents.ToDtoAsync(doc, ct));
        var edits = TailoringJson.Read<StoredEdits>(doc.Edits);
        if (doc.Status is not (TailoredStatus.Proposed or TailoredStatus.Failed) || edits is null || doc.FinalizedAt is not null)
            throw FieldErrors.Single("id", "These changes are not ready to finalise.");
        var accepted = edits.Items.Where(e => e.Decision == EditDecisions.Accepted).ToList();
        if (accepted.Count == 0)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, ErrorCodes.NoAcceptedEdits, "No accepted changes",
                "Accept at least one change before finalising.");
        await data.RequireConsentAsync(ct);
        await data.RequireBudgetAsync(current.UserId, ct);

        var resume = await db.Resumes.AsNoTracking().FirstAsync(r => r.Id == doc.ResumeId, ct);
        var model = await TailoringData.ExtractAsync(await data.FileAsync(resume.StorageKey, ct), ct);
        var paragraphs = model.Paragraphs.ToDictionary(p => p.Id, StringComparer.Ordinal);
        if (accepted.Any(e => !paragraphs.TryGetValue(e.ParagraphId, out var p) || p.Fingerprint != e.Fingerprint))
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.StaleEdit, "Résumé changed",
                "Your résumé no longer matches these changes. Generate them again.");

        // Re-check accepted new facts against the confirmed skills as they are now.
        var profile = await data.ProfileAsync(resume.Id, ct);
        var confirmed = await data.ConfirmedSkillsAsync(ct);
        var facts = TailoringData.Facts(model, profile, confirmed);
        foreach (var e in accepted.Where(e => e.Flags.Contains(EditFlags.NewFact)))
        {
            var p = paragraphs[e.ParagraphId];
            if (FactGuard.NewTerms(e.NewText[p.Flags.LockedPrefixLength..], facts, [], p.Section == ResumeSections.Skills) is { Count: > 0 } terms)
                throw FieldErrors.Single("id", $"Confirm {string.Join(", ", terms)} in your skills, or reject the change that adds it.");
        }

        doc.Status = TailoredStatus.Finalizing;
        doc.ErrorCode = null;
        doc.UpdatedAt = clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException
                                            || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw Conflict("Another tailoring for this job is in progress. Wait for it to finish.");
        }
        var docId = doc.Id;
        var userId = current.UserId;
        jobs.Enqueue<FinalizeResumeJob>(j => j.RunAsync(docId, userId, null, CancellationToken.None));
        return TypedResults.Accepted($"/api/v1/tailored-documents/{doc.Id}", await documents.ToDtoAsync(doc, ct));
    }

    /// <summary>The finished .docx (or PDF when <c>pdf_export.enabled</c>); only when ready.</summary>
    /// <param name="format"><c>docx</c> (default) or <c>pdf</c>.</param>
    private static async Task<FileContentHttpResult> Download(Guid id, string? format, HttpContext http, AppDbContext db,
        TailoringData data, TailoredDocuments documents, PdfExport pdf, ILoggerFactory loggers, CancellationToken ct)
    {
        var doc = await db.TailoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw DocumentNotFound();
        format = string.IsNullOrEmpty(format) ? TailoredDocuments.Docx : format.ToLowerInvariant();
        if (format is not (TailoredDocuments.Docx or TailoredDocuments.Pdf)) throw FieldErrors.Single("format", "Use docx or pdf.");
        if (doc.Status != TailoredStatus.Ready || doc.StorageKey is null) throw TailoringData.NotFound("This document is not ready yet.");
        if (format == TailoredDocuments.Pdf && !await documents.PdfEnabledAsync(ct))
            throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.FormatUnavailable, "Format unavailable", "PDF export is turned off.");

        var uj = await db.UserJobs.AsNoTracking().FirstAsync(u => u.Id == doc.UserJobId, ct);
        var bytes = await data.FileAsync(doc.StorageKey, ct);
        http.Response.Headers.XContentTypeOptions = "nosniff";
        if (format == TailoredDocuments.Docx)
            return TypedResults.File(bytes, ResumeEndpoints.DocxContentType, TailoredDocuments.FileName(doc, uj, format));
        try
        {
            return TypedResults.File(await pdf.ConvertAsync(bytes, ct), "application/pdf", TailoredDocuments.FileName(doc, uj, format));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogPdfFailed(loggers.CreateLogger(typeof(TailoringEndpoints)), doc.Id, ex.GetType().Name);
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServiceUnavailable, "PDF export unavailable",
                "The PDF could not be made right now. Download the .docx instead.");
        }
    }

    private static async Task<NoContent> DeleteDocument(Guid id, AppDbContext db, IFileStore files, CancellationToken ct)
    {
        var doc = await db.TailoredDocuments.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw DocumentNotFound();
        if (doc.StorageKey is not null) await files.DeleteAsync(doc.StorageKey, ct);
        db.TailoredDocuments.Remove(doc);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>The user's own edit of a ready cover letter (plain paragraphs); the stored .docx is written again.</summary>
    private static async Task<Ok<TailoredDocumentDto>> PutContent(Guid id, CoverLetterContentRequest body, AppDbContext db, IFileStore files,
        TailoredDocuments documents, TimeProvider clock, CancellationToken ct)
    {
        var doc = await db.TailoredDocuments.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw DocumentNotFound();
        if (doc.Kind != DocumentKinds.CoverLetter) throw FieldErrors.Single("paragraphs", "Only a cover letter can be edited this way.");
        if (doc.Status != TailoredStatus.Ready || doc.StorageKey is null)
            throw FieldErrors.Single("paragraphs", "The letter can be edited once it is ready.");
        var paragraphs = (body.Paragraphs ?? []).Select(p => p?.Trim() ?? "").Where(p => p.Length > 0).ToList();
        if (paragraphs.Count == 0) throw FieldErrors.Single("paragraphs", "The letter needs at least one paragraph.");
        if (paragraphs.Count > MaxLetterParagraphs) throw FieldErrors.Single("paragraphs", $"Use at most {MaxLetterParagraphs} paragraphs.");
        if (paragraphs.Sum(p => p.Length) > MaxLetterChars) throw FieldErrors.Single("paragraphs", $"Keep the letter under {MaxLetterChars} characters.");

        var bytes = CoverLetterWriter.Write(paragraphs);
        using (var s = new MemoryStream(bytes, writable: false)) await files.SaveAsync(doc.StorageKey, s, ct);
        var notes = TailoringJson.Read<StoredLetter>(doc.Content)?.Notes;
        doc.Content = TailoringJson.Write(new StoredLetter(paragraphs, Ai.Tasks.CoverLetterTask.WordCount(paragraphs), notes));
        doc.UpdatedAt = clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Conflict("The letter was changed in another tab. Reload and try again.");
        }
        return TypedResults.Ok(await documents.ToDtoAsync(doc, ct));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "PDF export of document {Id} failed ({ExceptionType})")]
    private static partial void LogPdfFailed(ILogger logger, Guid id, string exceptionType);
}
