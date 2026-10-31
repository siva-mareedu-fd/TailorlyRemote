using System.Text.Json;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tailorly.App.Ai;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Ports;
using Tailorly.Docx;
using Tailorly.Docx.Tailoring;

namespace Tailorly.App.Features.Tailoring;

/// <summary>Error codes of tailoring resources beyond the catalogue's (DECISIONS.md).</summary>
public static class TailoringErrors
{
    /// <summary>The edited document failed the OpenXML check (SDD §10.2 post-condition); nothing was changed.</summary>
    public const string FormatCheckFailed = "FORMAT_CHECK_FAILED";
}

/// <summary>Shared by the tailoring jobs: retries, the tracker's auto-move and failure bookkeeping.</summary>
internal static class TailoringJobSupport
{
    public const int Attempts = 2;

    /// <summary>The Hangfire retry count of this run; outside Hangfire (tests) every run is the last attempt.</summary>
    public static int RetryCount(PerformContext? context) => context?.GetJobParameter<int>("RetryCount") ?? Attempts;

    public static bool CanRetry(AiTaskFailedException ex, PerformContext? context) => ex.Transient && RetryCount(context) < Attempts;

    /// <summary>
    /// Saved → Tailored when a document is generated; the only automatic tracker move (PRD F11, SDD §12.3). Retried on a
    /// concurrent edit of the entry.
    /// </summary>
    public static async Task MarkTailoredAsync(AppDbContext db, Guid userJobId, DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var uj = await db.UserJobs.FirstOrDefaultAsync(u => u.Id == userJobId, ct);
            if (uj is null || uj.Status != UserJobStatus.Saved) return;
            uj.Status = UserJobStatus.Tailored;
            uj.StatusHistory = UserJobEndpoints.AppendHistory(uj.StatusHistory, UserJobStatus.Tailored, now);
            uj.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(uj).State = EntityState.Detached;
            }
        }
    }
}

/// <summary>
/// <c>ats_analysis</c> of the original résumé against the tracked job (PRD §8.3 step 1, SDD §7.4): extract, call the
/// model, compute the score deterministically, store requirements, gaps and format findings, and record the "before"
/// score on the tracker entry.
/// </summary>
public sealed partial class AtsAnalysisJob(AppDbContext db, TailoringData data, IAiTaskRunner ai, AtsScoring scoring, TimeProvider clock,
    ILogger<AtsAnalysisJob> logger)
{
    [AutomaticRetry(Attempts = TailoringJobSupport.Attempts, DelaysInSeconds = [60, 300])]
    public async Task RunAsync(Guid analysisId, Guid userId, PerformContext? context, CancellationToken ct)
    {
        using var _ = CurrentUser.ActAs(userId);
        var a = await db.AtsAnalyses.FirstOrDefaultAsync(x => x.Id == analysisId, ct);
        if (a is null || a.Status is not (WorkStatus.Queued or WorkStatus.Running)) return;
        a.Status = WorkStatus.Running;
        a.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        try
        {
            var uj = await db.UserJobs.AsNoTracking().FirstAsync(u => u.Id == a.UserJobId, ct);
            var resume = await db.Resumes.AsNoTracking().FirstAsync(r => r.Id == a.ResumeId, ct);
            var profile = await data.ProfileAsync(resume.Id, ct);
            var model = await TailoringData.ExtractAsync(await data.FileAsync(resume.StorageKey, ct), ct);
            var job = await data.JobAsync(uj, userId, ct);

            var result = await ai.RunAsync(AtsAnalysisTask.For(model), AtsAnalysisTask.BuildInput(job, model), userId, ct);
            var score = scoring.Score(model, profile, job.Enrichment, result.Value);

            a.AtsScore = (short)score.Score.Score;
            a.Components = TailoringJson.Write(new StoredScore(AtsComponentsDto.From(score.Score.Components),
                new KeywordsDto(score.Keywords.Matched, score.Keywords.Missing)));
            a.Requirements = TailoringJson.Write(result.Value.Requirements.Select(StoredRequirement.From).ToList());
            a.Gaps = TailoringJson.Write(AtsScoring.Gaps(result.Value, score.Keywords, AtsScoring.Lines(model)));
            a.Findings = TailoringJson.Write(score.Format.Findings.Select(f => new FindingDto(f.Code, f.Severity, f.Message, f.Suggestion)).ToList());
            a.Model = result.Model;
            a.PromptVersion = result.PromptVersion;
            a.Status = WorkStatus.Ready;
            a.ErrorCode = null;
            a.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await db.UserJobs.Where(u => u.Id == uj.Id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.AtsBefore, a.AtsScore).SetProperty(u => u.UpdatedAt, clock.GetUtcNow()), ct);
            LogAnalysed(logger, a.Id, score.Score.Score, result.Value.Requirements.Count, result.CountedTokens);
        }
        catch (AiTaskFailedException ex) when (TailoringJobSupport.CanRetry(ex, context))
        {
            throw; // Hangfire retries; the analysis stays "running" (the SPA shows "taking longer than usual")
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await FailAsync(a, Code(ex), ex, ct);
        }
    }

    internal static string Code(Exception ex) => ex switch
    {
        BudgetExceededException => ErrorCodes.BudgetExceeded,
        AiTaskFailedException f => f.Code,
        JobDescriptionMissingException => ErrorCodes.JobDescriptionRequired,
        _ => ErrorCodes.InternalError,
    };

    private async Task FailAsync(AtsAnalysis a, string code, Exception ex, CancellationToken ct)
    {
        a.Status = WorkStatus.Failed;
        a.ErrorCode = code;
        a.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None);
        LogFailed(logger, a.Id, code, ex.GetType().Name);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "ATS analysis {Id} ready: score {Score}, {RequirementCount} requirements, {CountedTokens} tokens")]
    private static partial void LogAnalysed(ILogger logger, Guid id, int score, int requirementCount, long countedTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ATS analysis {Id} failed with {ErrorCode} ({ExceptionType})")]
    private static partial void LogFailed(ILogger logger, Guid id, string errorCode, string exceptionType);
}

/// <summary>
/// <c>resume_tailor</c> (PRD §8.3 steps 3–5): the model proposes edit-only JSON for editable paragraphs, the validators
/// refuse contract breaks (one repair turn) and flag new facts, length and flattened formatting; the edits are stored as
/// <c>proposed</c> with their default decisions (new facts rejected) and a projected score.
/// </summary>
public sealed partial class TailorResumeJob(AppDbContext db, TailoringData data, IAiTaskRunner ai, AtsScoring scoring,
    IOptions<TailorOptions> options, TimeProvider clock, ILogger<TailorResumeJob> logger)
{
    [AutomaticRetry(Attempts = TailoringJobSupport.Attempts, DelaysInSeconds = [60, 300])]
    public async Task RunAsync(Guid documentId, Guid userId, PerformContext? context, CancellationToken ct)
    {
        using var _ = CurrentUser.ActAs(userId);
        var d = await db.TailoredDocuments.FirstOrDefaultAsync(x => x.Id == documentId && x.Kind == DocumentKinds.Resume, ct);
        if (d is null || d.Status is not (TailoredStatus.Queued or TailoredStatus.Running)) return;
        d.Status = TailoredStatus.Running;
        d.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        try
        {
            var uj = await db.UserJobs.AsNoTracking().FirstAsync(u => u.Id == d.UserJobId, ct);
            var analysis = await db.AtsAnalyses.AsNoTracking()
                .FirstOrDefaultAsync(a => a.UserJobId == d.UserJobId && a.ResumeId == d.ResumeId && a.Status == WorkStatus.Ready, ct);
            if (analysis is null)
            {
                await FailAsync(d, ErrorCodes.AnalysisRequired, null, ct);
                return;
            }
            var resume = await db.Resumes.AsNoTracking().FirstAsync(r => r.Id == d.ResumeId, ct);
            var profile = await data.ProfileAsync(resume.Id, ct);
            var confirmed = await data.ConfirmedSkillsAsync(ct);
            var model = await TailoringData.ExtractAsync(await data.FileAsync(resume.StorageKey, ct), ct);
            var job = await data.JobAsync(uj, userId, ct);

            var before = TailoringJson.Read<StoredScore>(analysis.Components)!;
            var requirements = TailoringJson.Read<List<StoredRequirement>>(analysis.Requirements) ?? [];
            var gaps = TailoringJson.Read<List<StoredGap>>(analysis.Gaps) ?? [];
            var rules = options.Value.Rules;
            var ctx = new TailorContext(model, TailoringData.Facts(model, profile, confirmed),
                TailoringData.SkillGroups(job.Enrichment, gaps.Select(g => g.Skill)), rules);
            var targets = new TailorTargets(
                requirements.Where(r => r.Status != Core.Ats.RequirementStatuses.Met).Select(r => r.Text).ToList(),
                before.Keywords.Missing, gaps.Select(g => (g.Skill, g.LikelyHas)).ToList());

            var result = await ai.RunAsync(ResumeTailorTask.For(ctx),
                ResumeTailorTask.BuildInput(job, model, targets, confirmed, rules), userId, ct);
            var edits = EditValidator.Check(ResumeTailorTask.Proposed(result.Value), ctx);
            var (projected, keywords) = scoring.Project(model, new Dictionary<string, string>(), job.Enrichment, before.Components);

            d.Edits = TailoringJson.Write(new StoredEdits(StoredEdits.CurrentVersion, edits.Select(StoredEdit.From).ToList(),
                result.Value.UnaddressedGaps.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Take(30).ToList()));
            d.AtsAfter = (short)projected.Score;
            d.AtsBreakdown = TailoringJson.Write(new StoredScore(AtsComponentsDto.From(projected.Components),
                new KeywordsDto(keywords.Matched, keywords.Missing), AtsBasis.Projected));
            d.Model = result.Model;
            d.PromptVersion = result.PromptVersion;
            d.Status = TailoredStatus.Proposed;
            d.ErrorCode = null;
            d.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            LogProposed(logger, d.Id, edits.Count, edits.Count(e => e.Flags.Contains(EditFlags.NewFact)), result.CountedTokens);
        }
        catch (AiTaskFailedException ex) when (TailoringJobSupport.CanRetry(ex, context))
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await FailAsync(d, AtsAnalysisJob.Code(ex), ex, ct);
        }
    }

    private async Task FailAsync(TailoredDocument d, string code, Exception? ex, CancellationToken ct)
    {
        d.Status = TailoredStatus.Failed;
        d.ErrorCode = code;
        d.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None);
        LogFailed(logger, d.Id, code, ex?.GetType().Name ?? "none");
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Tailored résumé {Id} proposed: {EditCount} edits, {NewFactCount} flagged new_fact, {CountedTokens} tokens")]
    private static partial void LogProposed(ILogger logger, Guid id, int editCount, int newFactCount, long countedTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tailored résumé {Id} failed with {ErrorCode} ({ExceptionType})")]
    private static partial void LogFailed(ILogger logger, Guid id, string errorCode, string exceptionType);
}

/// <summary>
/// Finalising (PRD §8.3 steps 6 and 8): apply the accepted edits to a copy of the original (SDD §10.2; pending counts as
/// rejected), store the file, re-score the final text with <c>ats_analysis</c> and record "after" on the tracker entry,
/// which moves Saved → Tailored. When the re-score cannot run (budget, AI down after the retries), the document is still
/// delivered and the projected score stands, labelled as such.
/// </summary>
public sealed partial class FinalizeResumeJob(AppDbContext db, TailoringData data, IAiTaskRunner ai, AtsScoring scoring, IFileStore files,
    TimeProvider clock, ILogger<FinalizeResumeJob> logger)
{
    [AutomaticRetry(Attempts = TailoringJobSupport.Attempts, DelaysInSeconds = [60, 300])]
    public async Task RunAsync(Guid documentId, Guid userId, PerformContext? context, CancellationToken ct)
    {
        using var _ = CurrentUser.ActAs(userId);
        var d = await db.TailoredDocuments.FirstOrDefaultAsync(x => x.Id == documentId && x.Kind == DocumentKinds.Resume, ct);
        if (d is null || d.Status != TailoredStatus.Finalizing) return;

        byte[] output;
        DocModel model;
        StoredEdits edits;
        try
        {
            edits = TailoringJson.Read<StoredEdits>(d.Edits) ?? throw new InvalidDataException("The edits are missing.");
            var accepted = edits.Items.Where(e => e.Decision == EditDecisions.Accepted).ToList();
            if (accepted.Count == 0)
            {
                await FailAsync(d, ErrorCodes.NoAcceptedEdits, null, ct);
                return;
            }
            var resume = await db.Resumes.AsNoTracking().FirstAsync(r => r.Id == d.ResumeId, ct);
            var original = await data.FileAsync(resume.StorageKey, ct);
            output = await DocxWork.RunAsync(() => EditApplier.Apply(original,
                accepted.Select(e => new EditToApply(e.ParagraphId, e.Fingerprint, e.NewText)).ToList()), ct);
            model = await TailoringData.ExtractAsync(output, ct);
            d.StorageKey = TailoredDocument.KeyFor(d.UserId, d.ResumeId, d.Id, d.Kind);
            using (var s = new MemoryStream(output, writable: false)) await files.SaveAsync(d.StorageKey, s, ct);
            d.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        catch (StaleEditException ex)
        {
            await FailAsync(d, ErrorCodes.StaleEdit, ex, ct);
            return;
        }
        catch (DocxValidationException ex)
        {
            LogInvalid(logger, d.Id, string.Join(",", ex.ParagraphIds));
            await FailAsync(d, TailoringErrors.FormatCheckFailed, ex, ct);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await FailAsync(d, ex is EditRejectedException ? TailoringErrors.FormatCheckFailed : ErrorCodes.InternalError, ex, ct);
            return;
        }

        StoredScore breakdown;
        short after;
        try
        {
            var uj = await db.UserJobs.AsNoTracking().FirstAsync(u => u.Id == d.UserJobId, ct);
            var profile = await data.ProfileAsync(d.ResumeId, ct);
            var job = await data.JobAsync(uj, userId, ct);
            var result = await ai.RunAsync(AtsAnalysisTask.For(model), AtsAnalysisTask.BuildInput(job, model), userId, ct);
            var score = scoring.Score(model, profile, job.Enrichment, result.Value);
            after = (short)score.Score.Score;
            breakdown = new StoredScore(AtsComponentsDto.From(score.Score.Components), new KeywordsDto(score.Keywords.Matched, score.Keywords.Missing),
                AtsBasis.Rescored, result.Value.Requirements.Select(StoredRequirement.From).ToList());
        }
        catch (AiTaskFailedException ex) when (TailoringJobSupport.CanRetry(ex, context))
        {
            throw; // the file is stored; the retry re-applies the same edits and re-scores
        }
        catch (Exception ex) when (ex is BudgetExceededException or AiTaskFailedException or JobDescriptionMissingException)
        {
            LogProjected(logger, d.Id, AtsAnalysisJob.Code(ex));
            breakdown = (TailoringJson.Read<StoredScore>(d.AtsBreakdown) ?? throw new InvalidDataException("No projection."))
                with { Basis = AtsBasis.Projected };
            after = d.AtsAfter ?? 0;
        }

        var now = clock.GetUtcNow();
        d.AtsAfter = after;
        d.AtsBreakdown = TailoringJson.Write(breakdown);
        d.Status = TailoredStatus.Ready;
        d.ErrorCode = null;
        d.FinalizedAt = now;
        d.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await db.UserJobs.Where(u => u.Id == d.UserJobId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.AtsAfter, after).SetProperty(u => u.UpdatedAt, now), ct);
        await TailoringJobSupport.MarkTailoredAsync(db, d.UserJobId, now, ct);
        LogFinalized(logger, d.Id, edits.Items.Count(e => e.Decision == EditDecisions.Accepted), after, output.Length);
    }

    private async Task FailAsync(TailoredDocument d, string code, Exception? ex, CancellationToken ct)
    {
        d.Status = TailoredStatus.Failed;
        d.ErrorCode = code;
        d.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None);
        LogFailed(logger, d.Id, code, ex?.GetType().Name ?? "none");
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Tailored résumé {Id} finalised: {AcceptedCount} edits applied, ATS after {AtsAfter}, {SizeBytes} bytes")]
    private static partial void LogFinalized(ILogger logger, Guid id, int acceptedCount, int atsAfter, int sizeBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tailored résumé {Id} re-score skipped ({ErrorCode}); the projected score stands")]
    private static partial void LogProjected(ILogger logger, Guid id, string errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tailored résumé {Id} failed the OpenXML check in paragraphs {ParagraphIds}")]
    private static partial void LogInvalid(ILogger logger, Guid id, string paragraphIds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tailored résumé {Id} finalising failed with {ErrorCode} ({ExceptionType})")]
    private static partial void LogFailed(ILogger logger, Guid id, string errorCode, string exceptionType);
}

/// <summary>
/// <c>cover_letter</c> (PRD F9, §8.4): a letter in the requested tone from the profile, the job and the tailored résumé's
/// accepted lines, checked like a tailoring (no invented facts), written to .docx from the template.
/// </summary>
public sealed partial class CoverLetterJob(AppDbContext db, TailoringData data, IAiTaskRunner ai, IFileStore files,
    IOptions<CoverLetterOptions> options, TimeProvider clock, ILogger<CoverLetterJob> logger)
{
    [AutomaticRetry(Attempts = TailoringJobSupport.Attempts, DelaysInSeconds = [60, 300])]
    public async Task RunAsync(Guid documentId, Guid userId, PerformContext? context, CancellationToken ct)
    {
        using var _ = CurrentUser.ActAs(userId);
        var d = await db.TailoredDocuments.FirstOrDefaultAsync(x => x.Id == documentId && x.Kind == DocumentKinds.CoverLetter, ct);
        if (d is null || d.Status is not (TailoredStatus.Queued or TailoredStatus.Running)) return;
        d.Status = TailoredStatus.Running;
        d.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        try
        {
            var uj = await db.UserJobs.AsNoTracking().FirstAsync(u => u.Id == d.UserJobId, ct);
            var resume = await db.Resumes.AsNoTracking().FirstAsync(r => r.Id == d.ResumeId, ct);
            var profile = await data.ProfileAsync(resume.Id, ct) ?? throw new InvalidDataException("The profile is missing.");
            var confirmed = await data.ConfirmedSkillsAsync(ct);
            var model = await TailoringData.ExtractAsync(await data.FileAsync(resume.StorageKey, ct), ct);
            var job = await data.JobAsync(uj, userId, ct);
            var notes = TailoringJson.Read<StoredLetter>(d.Content)?.Notes;
            var highlights = await HighlightsAsync(d, ct);

            var tone = d.Tone ?? CoverLetterOptions.DefaultTone;
            var maxWords = options.Value.EffectiveTones.GetValueOrDefault(tone, CoverLetterOptions.DefaultTones[CoverLetterOptions.DefaultTone]);
            var facts = TailoringData.Facts(model, profile, confirmed.Concat(highlights), job.Title, job.CompanyName, job.Description,
                job.Enrichment?.Summary, notes, CoverLetterTask.LetterVocabulary);
            var rules = new CoverLetterRules(tone, maxWords, options.Value.EffectiveCliches, job.CompanyName, job.Title, facts);

            var result = await ai.RunAsync(CoverLetterTask.For(rules),
                CoverLetterTask.BuildInput(job, profile, confirmed, highlights, notes, rules), userId, ct);
            var paragraphs = result.Value.Paragraphs.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            var bytes = CoverLetterWriter.Write(paragraphs);
            d.StorageKey = TailoredDocument.KeyFor(d.UserId, d.ResumeId, d.Id, d.Kind);
            using (var s = new MemoryStream(bytes, writable: false)) await files.SaveAsync(d.StorageKey, s, ct);

            var now = clock.GetUtcNow();
            d.Content = TailoringJson.Write(new StoredLetter(paragraphs, CoverLetterTask.WordCount(paragraphs), notes));
            d.Model = result.Model;
            d.PromptVersion = result.PromptVersion;
            d.Status = TailoredStatus.Ready;
            d.ErrorCode = null;
            d.FinalizedAt = now;
            d.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await TailoringJobSupport.MarkTailoredAsync(db, d.UserJobId, now, ct);
            LogWritten(logger, d.Id, paragraphs.Count, CoverLetterTask.WordCount(paragraphs), result.CountedTokens);
        }
        catch (AiTaskFailedException ex) when (TailoringJobSupport.CanRetry(ex, context))
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            d.Status = TailoredStatus.Failed;
            d.ErrorCode = AtsAnalysisJob.Code(ex);
            d.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
            LogFailed(logger, d.Id, d.ErrorCode, ex.GetType().Name);
        }
    }

    /// <summary>The accepted lines of the latest ready tailored résumé for the same job and résumé (at most eight).</summary>
    private async Task<List<string>> HighlightsAsync(TailoredDocument d, CancellationToken ct)
    {
        var json = await db.TailoredDocuments.AsNoTracking()
            .Where(x => x.UserJobId == d.UserJobId && x.ResumeId == d.ResumeId && x.Kind == DocumentKinds.Resume && x.Status == TailoredStatus.Ready)
            .OrderByDescending(x => x.FinalizedAt)
            .Select(x => x.Edits)
            .FirstOrDefaultAsync(ct);
        return TailoringJson.Read<StoredEdits>(json)?.Items.Where(e => e.Decision == EditDecisions.Accepted)
            .Select(e => ResumeExtractor.Normalize(e.NewText)).Take(8).ToList() ?? [];
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Cover letter {Id} ready: {ParagraphCount} paragraphs, {WordCount} words, {CountedTokens} tokens")]
    private static partial void LogWritten(ILogger logger, Guid id, int paragraphCount, int wordCount, long countedTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cover letter {Id} failed with {ErrorCode} ({ExceptionType})")]
    private static partial void LogFailed(ILogger logger, Guid id, string errorCode, string exceptionType);
}
