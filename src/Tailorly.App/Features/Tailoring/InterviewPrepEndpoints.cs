using Hangfire;
using Hangfire.Server;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tailorly.App.Ai;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Api;
using Tailorly.App.Features.Tracker;
using Tailorly.App.Infrastructure.Data;

namespace Tailorly.App.Features.Tailoring;

/// <summary>
/// Interview prep (ApiDesign §4.8, PRD F10): one per tracker entry, regenerated in place (charged each time). Starting it
/// follows the async pattern: <c>202</c> with the row the job fills, or <c>200</c> with the run already in flight. The
/// in-flight guard is a conditional update of the single row (<c>UNIQUE (user_job_id)</c>), so two requests cannot both
/// start a run.
/// </summary>
public static class InterviewPrepEndpoints
{
    public static void MapInterviewPrepEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/user-jobs/{id:guid}/interview-prep").WithTags("Tailoring").RequireAuthorization(AuthPolicies.User);
        g.MapPost("", Start).WithName("StartInterviewPrep").RequireRateLimiting(RateLimiting.AiStart);
        g.MapGet("", Get).WithName("GetInterviewPrep");
    }

    /// <summary>Generates (or regenerates) prep notes from the active résumé and the job. Same preconditions as the other AI routes.</summary>
    private static async Task<Results<Accepted<InterviewPrepDto>, Ok<InterviewPrepDto>>> Start(Guid id, AppDbContext db, TailoringData data,
        ICurrentUser current, IBackgroundJobClient jobs, TimeProvider clock, CancellationToken ct)
    {
        var uj = await data.UserJobAsync(id, ct);
        await data.RequireConsentAsync(ct);
        await data.ResumeAsync(null, ct);
        await data.RequireDescriptionAsync(uj, ct);

        var row = await db.InterviewPreps.AsNoTracking().FirstOrDefaultAsync(p => p.UserJobId == uj.Id, ct);
        if (row is { Status: WorkStatus.Queued or WorkStatus.Running }) return TypedResults.Ok(ToDto(row));
        await data.RequireBudgetAsync(current.UserId, ct);

        var now = clock.GetUtcNow();
        if (row is null)
        {
            row = new InterviewPrep { UserJobId = uj.Id, UserId = current.UserId, CreatedAt = now, UpdatedAt = now };
            db.InterviewPreps.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return TypedResults.Ok(ToDto(await db.InterviewPreps.AsNoTracking().FirstAsync(p => p.UserJobId == uj.Id, ct)));
            }
        }
        else
        {
            // Only one request can move a finished row back to queued; the loser returns the run it started.
            var claimed = await db.InterviewPreps
                .Where(p => p.Id == row.Id && p.Status != WorkStatus.Queued && p.Status != WorkStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, WorkStatus.Queued)
                    .SetProperty(p => p.ErrorCode, (string?)null)
                    .SetProperty(p => p.UpdatedAt, now), ct);
            row = await db.InterviewPreps.AsNoTracking().FirstAsync(p => p.Id == row.Id, ct);
            if (claimed == 0) return TypedResults.Ok(ToDto(row));
        }

        var (prepId, userId) = (row.Id, current.UserId);
        jobs.Enqueue<InterviewPrepJob>(j => j.RunAsync(prepId, userId, null, CancellationToken.None));
        return TypedResults.Accepted($"/api/v1/user-jobs/{uj.Id}/interview-prep", ToDto(row));
    }

    /// <summary>The entry's interview prep (404 if never generated). Poll here.</summary>
    private static async Task<Ok<InterviewPrepDto>> Get(Guid id, AppDbContext db, TailoringData data, CancellationToken ct)
    {
        await data.UserJobAsync(id, ct);
        var row = await db.InterviewPreps.AsNoTracking().FirstOrDefaultAsync(p => p.UserJobId == id, ct)
                  ?? throw TailoringData.NotFound("No interview prep has been generated for this job yet.");
        return TypedResults.Ok(ToDto(row));
    }

    public static InterviewPrepDto ToDto(InterviewPrep p)
    {
        var c = TailoringJson.Read<InterviewPrepOutput>(p.Content);
        var content = c is null
            ? null
            : new InterviewPrepContentDto(
                c.TechnicalTopics.Select(t => new PrepTopicDto(t.Topic, t.Why)).ToList(),
                c.Behavioural.Select(b => new PrepQuestionDto(b.Question, b.TalkingPoints)).ToList(),
                c.GapsToPrepare, c.QuestionsToAsk);
        // Content is shown only when ready: a regeneration in flight or failed does not show the previous notes as current.
        return new InterviewPrepDto(p.Id, p.UserJobId, p.Status, p.ErrorCode, p.Status == WorkStatus.Ready ? content : null, p.UpdatedAt);
    }
}

/// <summary>
/// <c>interview_prep</c> (PRD F10): the active résumé's profile and the job, checked so talking points use only the
/// candidate's own facts (<see cref="InterviewPrepTask"/>). Same retry and failure handling as the other tailoring jobs.
/// </summary>
public sealed partial class InterviewPrepJob(AppDbContext db, TailoringData data, IAiTaskRunner ai, TimeProvider clock,
    ILogger<InterviewPrepJob> logger)
{
    [AutomaticRetry(Attempts = TailoringJobSupport.Attempts, DelaysInSeconds = [60, 300])]
    public async Task RunAsync(Guid prepId, Guid userId, PerformContext? context, CancellationToken ct)
    {
        using var _ = CurrentUser.ActAs(userId);
        var p = await db.InterviewPreps.FirstOrDefaultAsync(x => x.Id == prepId, ct);
        if (p is null || p.Status is not (WorkStatus.Queued or WorkStatus.Running)) return;
        p.Status = WorkStatus.Running;
        p.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        try
        {
            var uj = await db.UserJobs.AsNoTracking().FirstAsync(u => u.Id == p.UserJobId, ct);
            var resume = await data.ResumeAsync(null, ct);
            var profile = await data.ProfileAsync(resume.Id, ct) ?? throw new InvalidDataException("The profile is missing.");
            var confirmed = await data.ConfirmedSkillsAsync(ct);
            var model = await TailoringData.ExtractAsync(await data.FileAsync(resume.StorageKey, ct), ct);
            var job = await data.JobAsync(uj, userId, ct);

            var resumeFacts = TailoringData.Facts(model, profile, confirmed, job.Title, job.CompanyName);
            var allFacts = TailoringData.Facts(model, profile, confirmed, job.Title, job.CompanyName, job.Description, job.Enrichment?.Summary,
                job.Enrichment is { } e ? string.Join("\n", e.SkillsRequired.Concat(e.SkillsPreferred)) : null);
            var result = await ai.RunAsync(InterviewPrepTask.For(new InterviewPrepRules(resumeFacts, allFacts)),
                InterviewPrepTask.BuildInput(job, profile, confirmed), userId, ct);

            p.Content = TailoringJson.Write(result.Value);
            p.Model = result.Model;
            p.PromptVersion = result.PromptVersion;
            p.Status = WorkStatus.Ready;
            p.ErrorCode = null;
            p.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            LogReady(logger, p.Id, result.Value.TechnicalTopics.Count, result.Value.Behavioural.Count, result.CountedTokens);
        }
        catch (AiTaskFailedException ex) when (TailoringJobSupport.CanRetry(ex, context))
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            p.Status = WorkStatus.Failed;
            p.ErrorCode = ex is ApiException { Code: { } code } ? code : AtsAnalysisJob.Code(ex);
            p.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
            LogFailed(logger, p.Id, p.ErrorCode, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Interview prep {Id} ready: {TopicCount} topics, {QuestionCount} behavioural questions, {CountedTokens} tokens")]
    private static partial void LogReady(ILogger logger, Guid id, int topicCount, int questionCount, long countedTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Interview prep {Id} failed with {ErrorCode} ({ExceptionType})")]
    private static partial void LogFailed(ILogger logger, Guid id, string errorCode, string exceptionType);
}
