using System.Text.Json;
using Hangfire;
using Hangfire.Server;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Ai;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Api;
using Tailorly.App.Features.Digests;
using Tailorly.App.Features.Jobs;
using Tailorly.App.Features.Tailoring;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Jobs;

namespace Tailorly.App.Features.Tracker;

/// <summary>
/// <c>POST /jobs/byo</c> (ApiDesign §4.6, PRD F12, SDD §8.0 path C): a job the user found elsewhere (LinkedIn, Naukri, a
/// company site), pasted as text. It becomes a tracker entry like any other; the optional link is stored as text and
/// <b>never fetched</b> (ADR-008).
/// </summary>
public static class ByoJobEndpoints
{
    public const string SourceKey = "byo";
    public const int MaxTitleChars = 200;
    public const int MaxUrlChars = 2_000;
    public const string DefaultTitle = "Untitled role";
    public const string DefaultCompany = "Company not given";

    public static void MapByoJobEndpoints(this RouteGroupBuilder api) =>
        api.MapPost("/jobs/byo", Create).WithName("AddByoJob").WithTags("Tracker").RequireAuthorization(AuthPolicies.User)
            .RequireRateLimiting(RateLimiting.AiStart);

    /// <summary>
    /// Stores the pasted job as a Saved tracker entry and starts its enrichment (<c>byo_job_enrichment</c>, charged to
    /// Documents) and fit score in the background → <c>202</c> with <c>enrichmentStatus: queued</c>. Poll
    /// <c>GET /user-jobs/{id}</c>.
    /// </summary>
    private static async Task<Accepted<UserJobDto>> Create(ByoJobRequest body, AppDbContext db, TailoringData data, ICurrentUser current,
        IBackgroundJobClient jobs, TimeProvider clock, CancellationToken ct)
    {
        var errors = new FieldErrors();
        var description = errors.Text("description", body.Description, UserJobEndpoints.MaxDescriptionChars, label: "The description");
        var title = errors.Text("title", body.Title, MaxTitleChars, required: false, label: "Title");
        var company = errors.Text("companyName", body.CompanyName, MaxTitleChars, required: false, label: "Company");
        var url = errors.Text("url", body.Url, MaxUrlChars, required: false, label: "Link");
        if (url is not null && !ScoredJobs.IsHttp(url)) errors.Add("url", "Use a link that starts with http:// or https://.");
        errors.ThrowIfAny();
        await data.RequireConsentAsync(ct);
        await data.RequireBudgetAsync(current.UserId, ct);

        var sourceName = await db.JobSources.AsNoTracking().Where(s => s.Key == SourceKey).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
        var now = clock.GetUtcNow();
        var uj = new UserJob
        {
            UserId = current.UserId,
            IsByo = true,
            Title = title ?? DefaultTitle,
            CompanyName = company ?? DefaultCompany,
            ApplyUrl = url,
            SourceKey = SourceKey,
            SourceName = sourceName ?? "Added by you",
            UserDescription = description,
            EnrichmentStatus = WorkStatus.Queued,
            StatusHistory = UserJobEndpoints.AppendHistory("[]", UserJobStatus.Saved, now),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.UserJobs.Add(uj);
        await db.SaveChangesAsync(ct);

        var (userJobId, userId) = (uj.Id, current.UserId);
        jobs.Enqueue<ByoJobEnrichmentJob>(j => j.RunAsync(userJobId, userId, null, CancellationToken.None));
        return TypedResults.Accepted($"/api/v1/user-jobs/{uj.Id}", await UserJobEndpoints.ToDtoAsync(db, uj, ct));
    }
}

/// <summary>
/// The background half of a BYO job: <c>byo_job_enrichment</c> of the pasted text (Documents; the same prompt, schema and
/// eligibility resolution as listings), then <c>job_fit_score</c> against the active résumé (Discovery, as for every fit
/// score). The fit is optional: without a ready résumé, with Discovery used up, or when the model fails, the entry is
/// ready without a score. Results are stored on the entry (<c>user_enrichment</c>, <c>fit_score</c>).
/// </summary>
public sealed partial class ByoJobEnrichmentJob(AppDbContext db, TailoringData data, IAiTaskRunner ai, TimeProvider clock,
    ILogger<ByoJobEnrichmentJob> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [AutomaticRetry(Attempts = TailoringJobSupport.Attempts, DelaysInSeconds = [60, 300])]
    public async Task RunAsync(Guid userJobId, Guid userId, PerformContext? context, CancellationToken ct)
    {
        using var _ = CurrentUser.ActAs(userId);
        var uj = await db.UserJobs.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userJobId, ct);
        if (uj is null || uj.UserDescription is null || uj.EnrichmentStatus is not (WorkStatus.Queued or WorkStatus.Running)) return;
        await SetStatusAsync(uj.Id, WorkStatus.Running, null, ct);
        uj.EnrichmentStatus = WorkStatus.Running;

        try
        {
            var job = await data.JobAsync(uj, userId, ct);
            var stored = ScoredJobs.ParseEnrichment(await db.UserJobs.AsNoTracking().Where(u => u.Id == uj.Id)
                .Select(u => u.UserEnrichment).FirstAsync(ct)) ?? throw new InvalidDataException("The enrichment is missing.");

            var (fitScore, fit) = await FitAsync(uj, job, context, ct);
            var json = JsonSerializer.Serialize(stored with { Fit = fit }, Json);
            await db.UserJobs.Where(u => u.Id == uj.Id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.UserEnrichment, json)
                .SetProperty(u => u.FitScore, fitScore)
                .SetProperty(u => u.EnrichmentStatus, WorkStatus.Ready)
                .SetProperty(u => u.EnrichmentErrorCode, (string?)null)
                .SetProperty(u => u.UpdatedAt, clock.GetUtcNow()), ct);
            LogReady(logger, uj.Id, fitScore is not null);
        }
        catch (AiTaskFailedException ex) when (TailoringJobSupport.CanRetry(ex, context))
        {
            throw; // Hangfire retries; the entry stays "running"
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var code = AtsAnalysisJob.Code(ex);
            await SetStatusAsync(uj.Id, WorkStatus.Failed, code, CancellationToken.None);
            LogFailed(logger, uj.Id, code, ex.GetType().Name);
        }
    }

    /// <summary>The fit score of the pasted job, or (null, null) when it cannot be made now (see the class remarks).</summary>
    private async Task<(short? Score, StoredFit? Fit)> FitAsync(UserJob uj, TailoringJob job, PerformContext? context, CancellationToken ct)
    {
        if (job.Enrichment is not { } e) return (null, null);
        var ctx = await MatchContext.LoadAsync(db, uj.UserId, ct);
        if (ctx is null) return (null, null);
        var p = ctx.Preferences;
        var summary = JobFitScoreTask.CandidateSummary(ctx.Profile, ctx.ConfirmedSkills,
            new FitPreferences(p.TargetRoles, p.ExperienceLevel, p.TimezoneOverlap));
        var facts = new FitJobFacts(uj.Title, uj.CompanyName, null, e.WorkMode, e.EmploymentType, e.Eligibility, Completeness.Full, e);
        try
        {
            var result = await ai.RunAsync(JobFitScoreTask.Definition, JobFitScoreTask.BuildInput(summary, facts), uj.UserId, ct);
            var f = result.Value;
            return ((short)f.FitScore, new StoredFit(f.Reasons, f.MatchedSkills, f.MissingSkills, f.Confidence));
        }
        catch (BudgetExceededException)
        {
            LogNoFit(logger, uj.Id, ErrorCodes.BudgetExceeded);
            return (null, null);
        }
        catch (AiTaskFailedException ex) when (!TailoringJobSupport.CanRetry(ex, context))
        {
            LogNoFit(logger, uj.Id, ex.Code);
            return (null, null);
        }
    }

    private Task SetStatusAsync(Guid id, string status, string? errorCode, CancellationToken ct) =>
        db.UserJobs.Where(u => u.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.EnrichmentStatus, status)
            .SetProperty(u => u.EnrichmentErrorCode, errorCode)
            .SetProperty(u => u.UpdatedAt, clock.GetUtcNow()), ct);

    [LoggerMessage(Level = LogLevel.Information, Message = "BYO job {Id} enriched (fit score: {HasFitScore})")]
    private static partial void LogReady(ILogger logger, Guid id, bool hasFitScore);

    [LoggerMessage(Level = LogLevel.Information, Message = "BYO job {Id} has no fit score ({ErrorCode})")]
    private static partial void LogNoFit(ILogger logger, Guid id, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "BYO job {Id} enrichment failed with {ErrorCode} ({ExceptionType})")]
    private static partial void LogFailed(ILogger logger, Guid id, string errorCode, string exceptionType);
}
