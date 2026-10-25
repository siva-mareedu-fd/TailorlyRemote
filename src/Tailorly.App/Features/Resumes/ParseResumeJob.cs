using System.Text.Json;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Ai;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Ports;
using Tailorly.Docx;

namespace Tailorly.App.Features.Resumes;

/// <summary>
/// <c>ParseResume</c> (SDD §7.1): layout analysis and extraction (bounded concurrency 2, 60 s timeout), then the
/// <c>resume_parse</c> AI task; saves <c>resume_profiles</c> and makes the version active once it is ready.
/// Idempotent: a résumé that is no longer queued/running is left alone. Transient AI failures are retried by Hangfire
/// (1 and 5 minutes, inside the 30-minute stuck-work sweep); every other failure ends the résumé as
/// <c>failed</c> with an error code.
/// </summary>
public sealed partial class ParseResumeJob(
    AppDbContext db,
    IFileStore files,
    IAiTaskRunner ai,
    ILogger<ParseResumeJob> logger)
{
    private const int Attempts = 2;
    private static readonly SemaphoreSlim DocxGate = new(2, 2);

    [AutomaticRetry(Attempts = Attempts, DelaysInSeconds = [60, 300])]
    public async Task RunAsync(Guid resumeId, Guid userId, PerformContext? context, CancellationToken ct)
    {
        using var _ = CurrentUser.ActAs(userId);
        var resume = await db.Resumes.FirstOrDefaultAsync(r => r.Id == resumeId, ct);
        if (resume is null || resume.ParseStatus is not (WorkStatus.Queued or WorkStatus.Running))
            return;

        resume.ParseStatus = WorkStatus.Running;
        await db.SaveChangesAsync(ct);

        DocModel model;
        try
        {
            model = await ExtractAsync(resume, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogExtractFailed(logger, ex, resumeId, ex.GetType().Name);
            await FailAsync(resume, ErrorCodes.UnsupportedFileType, ct);
            return;
        }

        resume.FormatFindings = JsonSerializer.Serialize(model.Findings, ResumeDto.Json);
        await db.SaveChangesAsync(ct);

        var text = ResumeText.Build(model);
        if (text.Length == 0)
        {
            await FailAsync(resume, ErrorCodes.UnsupportedFileType, ct);
            return;
        }

        try
        {
            var result = await ai.RunAsync(ResumeParseTask.Definition, new AiTaskInput([new UntrustedBlock("resume_data", text)]),
                userId, ct);
            await SaveProfileAsync(resume, result, ct);
            LogParsed(logger, resumeId, model.Paragraphs.Count, text.Length, result.CountedTokens);
        }
        catch (BudgetExceededException)
        {
            await FailAsync(resume, ErrorCodes.BudgetExceeded, ct);
        }
        catch (AiTaskFailedException ex) when (ex.Transient && RetryCount(context) < Attempts)
        {
            LogWillRetry(logger, resumeId, RetryCount(context));
            throw; // Hangfire retries; the résumé stays "running" (the SPA shows "taking longer than usual")
        }
        catch (AiTaskFailedException ex)
        {
            await FailAsync(resume, ex.Code, ct);
        }
    }

    private async Task<DocModel> ExtractAsync(Resume resume, CancellationToken ct)
    {
        await using var stream = await files.OpenReadAsync(resume.StorageKey, ct)
            ?? throw new FileNotFoundException("The stored résumé file is missing.");
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        copy.Position = 0;

        await DocxGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => ResumeExtractor.Extract(copy), ct).WaitAsync(DocxLimits.ParseTimeout, ct);
        }
        finally
        {
            DocxGate.Release();
        }
    }

    private async Task SaveProfileAsync(Resume resume, AiTaskResult<ParsedProfile> result, CancellationToken ct)
    {
        var parsed = JsonSerializer.Serialize(result.Value, ResumeDto.Json);
        var profile = await db.ResumeProfiles.FirstOrDefaultAsync(p => p.ResumeId == resume.Id, ct);
        if (profile is null)
        {
            db.ResumeProfiles.Add(new ResumeProfile
            {
                ResumeId = resume.Id,
                UserId = resume.UserId,
                Parsed = parsed,
                PromptVersion = result.PromptVersion,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            profile.Parsed = parsed;
            profile.PromptVersion = result.PromptVersion;
            profile.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // The new version becomes active when its parse is ready; the previous active one is deactivated (ApiDesign §4.4).
        resume.ParseStatus = WorkStatus.Ready;
        resume.ErrorCode = null;
        await ResumeEndpoints.ActivateAsync(db, resume, ct);
    }

    private async Task FailAsync(Resume resume, string code, CancellationToken ct)
    {
        resume.ParseStatus = WorkStatus.Failed;
        resume.ErrorCode = code;
        await db.SaveChangesAsync(ct);
        LogFailed(logger, resume.Id, code);
    }

    private static int RetryCount(PerformContext? context) => context?.GetJobParameter<int>("RetryCount") ?? Attempts;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Résumé {Id} parsed: {ParagraphCount} paragraphs, {TextLength} chars, {CountedTokens} tokens")]
    private static partial void LogParsed(ILogger logger, Guid id, int paragraphCount, int textLength, long countedTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Résumé {Id} parse failed with {ErrorCode}")]
    private static partial void LogFailed(ILogger logger, Guid id, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Résumé {Id} could not be extracted ({ExceptionType})")]
    private static partial void LogExtractFailed(ILogger logger, Exception ex, Guid id, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Résumé {Id} parse will be retried (attempt {RetryCount})")]
    private static partial void LogWillRetry(ILogger logger, Guid id, int retryCount);
}

/// <summary>Stuck-work sweep (every 15 min, ApiDesign §3.5): rows queued/running for over 30 minutes become TIMEOUT.</summary>
public sealed partial class SweepStuckWorkJob(SystemQueries system, TimeProvider clock, ILogger<SweepStuckWorkJob> logger)
{
    public const string RecurringId = "sweep-stuck-work";
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(30);

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - StuckAfter;
        var resumes = await system.FailStuckResumesAsync(cutoff, ErrorCodes.Timeout, ct);
        // Analyses, tailored documents, interview preps and digests join this sweep with their tables (M3–M5).
        if (resumes > 0) LogSwept(logger, resumes);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stuck-work sweep marked {ResumeCount} résumé(s) as TIMEOUT")]
    private static partial void LogSwept(ILogger logger, int resumeCount);
}
