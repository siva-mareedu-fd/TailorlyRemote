using System.Globalization;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tailorly.App.Api;
using Tailorly.App.Features.Jobs;
using Tailorly.App.Features.Tailoring;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Tracker;

/// <summary>
/// The application tracker (ApiDesign §4.7, PRD F11): tracking a scored job, the board's list, reading an entry, moving
/// it between statuses (any status to any other, with history) and editing notes under the concurrency token, pasting a
/// description, listing its documents, and deleting it with everything made for it. Every id resolves through the
/// user-scoped context, so another user's entry is a 404.
/// </summary>
public static class UserJobEndpoints
{
    public const int MaxDescriptionChars = 30_000;
    public const int MaxNotesChars = 2_000;
    public const int MaxPageSize = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapUserJobEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/user-jobs").WithTags("Tracker").RequireAuthorization(AuthPolicies.User);
        g.MapPost("", Create).WithName("TrackJob");
        g.MapGet("", List).WithName("ListUserJobs");
        g.MapGet("/{id:guid}", Get).WithName("GetUserJob");
        g.MapPatch("/{id:guid}", Update).WithName("UpdateUserJob");
        g.MapPut("/{id:guid}/description", PutDescription).WithName("PutUserJobDescription");
        g.MapDelete("/{id:guid}", Delete).WithName("DeleteUserJob");
        g.MapGet("/{id:guid}/tailored-documents", ListDocuments).WithName("ListTailoredDocuments");
    }

    private static ApiException Stale() => new(StatusCodes.Status409Conflict, ErrorCodes.ConcurrentUpdate, "Changed meanwhile",
        "This job was changed in another tab. Reload and try again.");

    /// <summary>
    /// Tracks a job scored for the caller: snapshots title, company, apply link and source (so the entry outlives the
    /// listing) and copies the fit score. Idempotent per job: tracking it again returns the entry with 200.
    /// </summary>
    private static async Task<Results<Created<UserJobDto>, Ok<UserJobDto>>> Create(CreateUserJobRequest body, AppDbContext db,
        ICurrentUser current, TimeProvider clock, CancellationToken ct)
    {
        if (body.JobId is not { } jobId) throw FieldErrors.Single("jobId", "Choose a job.");
        if (await db.UserJobs.AsNoTracking().FirstOrDefaultAsync(u => u.JobId == jobId, ct) is { } existing)
            return TypedResults.Ok(await ToDtoAsync(db, existing, ct));

        // Only jobs scored for the caller are visible (ApiDesign §4.6); the shared table is never reachable by id alone.
        var score = await db.UserJobScores.AsNoTracking().Where(s => s.JobId == jobId)
            .OrderByDescending(s => s.ResumeVersion).ThenByDescending(s => s.CreatedAt)
            .Select(s => new { s.FitScore, s.Model }).FirstOrDefaultAsync(ct)
            ?? throw TailoringData.NotFound("This job is not available.");
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct) ?? throw TailoringData.NotFound("This job is not available.");
        var source = await db.JobSources.AsNoTracking().FirstOrDefaultAsync(s => s.Key == job.SourceKey, ct);

        var now = clock.GetUtcNow();
        var uj = new UserJob
        {
            UserId = current.UserId,
            JobId = job.Id,
            Title = job.Title,
            CompanyName = job.CompanyName,
            ApplyUrl = ScoredJobs.IsHttp(job.ApplyUrl) ? job.ApplyUrl : job.SourceUrl,
            SourceKey = job.SourceKey,
            SourceName = source?.DisplayName ?? job.SourceKey,
            AttributionUrl = source?.AttributionUrl,
            FitScore = score.Model == UserJobScore.RulesModel ? null : score.FitScore,
            StatusHistory = AppendHistory("[]", UserJobStatus.Saved, now),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.UserJobs.Add(uj);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(uj).State = EntityState.Detached;
            var raced = await db.UserJobs.AsNoTracking().FirstAsync(u => u.JobId == jobId, ct);
            return TypedResults.Ok(await ToDtoAsync(db, raced, ct));
        }
        return TypedResults.Created($"/api/v1/user-jobs/{uj.Id}", await ToDtoAsync(db, uj, ct));
    }

    /// <summary>The caller's tracker entries (the board and the list view).</summary>
    /// <param name="status">Repeatable: saved | tailored | applied | interviewing | offer | rejected | withdrawn.</param>
    /// <param name="q">Text in the title or company name.</param>
    /// <param name="sort"><c>-updated</c> (default) or <c>-score</c> (fit score, unscored last).</param>
    /// <param name="pageSize">1–100, default 50.</param>
    private static async Task<Ok<UserJobPage>> List(AppDbContext db, [FromQuery] string[]? status, string? q, string? sort, int? page,
        int? pageSize, CancellationToken ct)
    {
        var errors = new FieldErrors();
        if (status?.Any(s => !UserJobStatus.All.Contains(s, StringComparer.Ordinal)) == true)
            errors.Add("status", $"Allowed values: {string.Join(", ", UserJobStatus.All)}.");
        if (sort is not (null or "-updated" or "-score")) errors.Add("sort", "Use -updated or -score.");
        if (q is { Length: > 100 }) errors.Add("q", "Search text must be 100 characters or fewer.");
        errors.ThrowIfAny();

        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 50, 1, MaxPageSize);
        var query = db.UserJobs.AsNoTracking();
        if (status is { Length: > 0 }) query = query.Where(u => status.Contains(u.Status));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            query = query.Where(u => EF.Functions.ILike(u.Title, pattern, "\\") || EF.Functions.ILike(u.CompanyName, pattern, "\\"));
        }
        query = sort == "-score"
            ? query.OrderBy(u => u.FitScore == null).ThenByDescending(u => u.FitScore).ThenByDescending(u => u.UpdatedAt).ThenBy(u => u.Id)
            : query.OrderByDescending(u => u.UpdatedAt).ThenBy(u => u.Id);

        var total = await query.CountAsync(ct);
        var rows = await query.Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return TypedResults.Ok(new UserJobPage(await ToDtosAsync(db, rows, ct), p, size, total));
    }

    private static async Task<Ok<UserJobDto>> Get(Guid id, AppDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await ToDtoAsync(db, await db.UserJobs.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw TailoringData.NotFound(), ct));

    /// <summary>
    /// Moves the entry to any status (no state machine, SDD S9) and/or edits its notes. A status change appends
    /// <c>{status, at}</c> to the history; setting the current status again changes nothing. A stale
    /// <c>concurrencyToken</c> is 409 <c>CONCURRENT_UPDATE</c>.
    /// </summary>
    private static async Task<Ok<UserJobDto>> Update(Guid id, UpdateUserJobRequest body, AppDbContext db, TimeProvider clock,
        CancellationToken ct)
    {
        var uj = await db.UserJobs.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw TailoringData.NotFound();
        var errors = new FieldErrors();
        if (string.IsNullOrEmpty(body.ConcurrencyToken)) errors.Add("concurrencyToken", "Send the concurrencyToken of the entry you last read.");
        if (body.Status is not null && !UserJobStatus.All.Contains(body.Status, StringComparer.Ordinal))
            errors.Add("status", $"Allowed values: {string.Join(", ", UserJobStatus.All)}.");
        if (body.Status is null && body.Notes is null) errors.Add("status", "Send a status, notes, or both.");
        var notes = body.Notes is null ? null : errors.Text("notes", body.Notes, MaxNotesChars, required: false, label: "Notes");
        errors.ThrowIfAny();
        if (body.ConcurrencyToken != Token(uj)) throw Stale();

        var now = clock.GetUtcNow();
        if (body.Status is { } next && next != uj.Status)
        {
            uj.Status = next;
            uj.StatusHistory = AppendHistory(uj.StatusHistory, next, now);
        }
        if (body.Notes is not null) uj.Notes = notes;
        uj.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Stale();
        }
        return TypedResults.Ok(await ToDtoAsync(db, uj, ct));
    }

    /// <summary>
    /// Stores a pasted description (for a limited listing or one that is gone) and clears its enrichment, which is made
    /// again on the next AI action. For a BYO job the enrichment and fit score run again in the background at once (the
    /// way to retry a failed one). The text is stored, never fetched from anywhere.
    /// </summary>
    private static async Task<Ok<UserJobDto>> PutDescription(Guid id, UserJobDescriptionRequest body, AppDbContext db, TailoringData data,
        ICurrentUser current, IBackgroundJobClient jobs, TimeProvider clock, CancellationToken ct)
    {
        var uj = await db.UserJobs.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw TailoringData.NotFound();
        var errors = new FieldErrors();
        var text = errors.Text("description", body.Description, MaxDescriptionChars, label: "The description");
        errors.ThrowIfAny();
        if (uj.IsByo)
        {
            await data.RequireConsentAsync(ct);
            await data.RequireBudgetAsync(current.UserId, ct);
        }
        uj.UserDescription = text;
        uj.UserEnrichment = null;
        uj.EnrichmentStatus = uj.IsByo ? WorkStatus.Queued : null;
        uj.EnrichmentErrorCode = null;
        uj.UpdatedAt = clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Stale();
        }
        if (uj.IsByo)
        {
            var (userJobId, userId) = (uj.Id, current.UserId);
            jobs.Enqueue<ByoJobEnrichmentJob>(j => j.RunAsync(userJobId, userId, null, CancellationToken.None));
        }
        return TypedResults.Ok(await ToDtoAsync(db, uj, ct));
    }

    /// <summary>
    /// Deletes the entry with its analysis, tailored documents (files first) and interview prep (ApiDesign §4.7). The
    /// listing itself is shared and stays.
    /// </summary>
    private static async Task<NoContent> Delete(Guid id, AppDbContext db, IFileStore files, CancellationToken ct)
    {
        var uj = await db.UserJobs.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw TailoringData.NotFound();
        var keys = await db.TailoredDocuments.AsNoTracking().Where(d => d.UserJobId == id && d.StorageKey != null)
            .Select(d => d.StorageKey!).ToListAsync(ct);
        foreach (var key in keys) await files.DeleteAsync(key, ct);
        db.UserJobs.Remove(uj);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<TailoredDocumentDto>>> ListDocuments(Guid id, AppDbContext db, TailoredDocuments documents,
        CancellationToken ct)
    {
        if (!await db.UserJobs.AnyAsync(u => u.Id == id, ct)) throw TailoringData.NotFound();
        var rows = await db.TailoredDocuments.AsNoTracking().Where(d => d.UserJobId == id).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(await documents.ToDtosAsync(rows, ct));
    }

    public static string Token(UserJob uj) => uj.Version.ToString(CultureInfo.InvariantCulture);

    public static async Task<UserJobDto> ToDtoAsync(AppDbContext db, UserJob uj, CancellationToken ct) =>
        (await ToDtosAsync(db, [uj], ct))[0];

    /// <summary>Entries with their document statuses, in a fixed number of queries whatever the page size.</summary>
    public static async Task<List<UserJobDto>> ToDtosAsync(AppDbContext db, IReadOnlyList<UserJob> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.Id).ToList();
        var jobIds = rows.Where(r => r.JobId is not null).Select(r => r.JobId!.Value).ToList();
        var withText = (await db.Jobs.AsNoTracking().Where(j => jobIds.Contains(j.Id) && j.DescriptionText != null)
            .Select(j => j.Id).ToListAsync(ct)).ToHashSet();
        var analyses = (await db.AtsAnalyses.AsNoTracking().Where(a => ids.Contains(a.UserJobId))
                .Select(a => new { a.UserJobId, a.Status, a.UpdatedAt }).ToListAsync(ct))
            .GroupBy(a => a.UserJobId).ToDictionary(g => g.Key, g => g.MaxBy(a => a.UpdatedAt)!.Status);
        var documents = (await db.TailoredDocuments.AsNoTracking().Where(d => ids.Contains(d.UserJobId))
                .Select(d => new { d.UserJobId, d.Kind, d.Status, d.CreatedAt }).ToListAsync(ct))
            .GroupBy(d => (d.UserJobId, d.Kind)).ToDictionary(g => g.Key, g => g.MaxBy(d => d.CreatedAt)!.Status);
        var preps = await db.InterviewPreps.AsNoTracking().Where(p => ids.Contains(p.UserJobId))
            .ToDictionaryAsync(p => p.UserJobId, p => p.Status, ct);

        return rows.Select(uj =>
        {
            var history = JsonSerializer.Deserialize<List<StatusChangeDto>>(uj.StatusHistory, Json) ?? [];
            var pasted = uj.UserDescription is not null;
            return new UserJobDto(uj.Id, uj.JobId, uj.IsByo, uj.Title, uj.CompanyName, uj.ApplyUrl,
                uj.SourceKey is null ? null : new UserJobSourceDto(uj.SourceKey, uj.SourceName ?? uj.SourceKey, uj.AttributionUrl),
                uj.Status, uj.Notes, uj.FitScore, uj.AtsBefore, uj.AtsAfter,
                HasUserDescription: pasted,
                DescriptionAvailable: pasted || (uj.JobId is { } jobId && withText.Contains(jobId)),
                EnrichmentStatus: !pasted ? null : uj.EnrichmentStatus ?? (uj.UserEnrichment is null ? WorkStatus.Queued : WorkStatus.Ready),
                EnrichmentErrorCode: pasted ? uj.EnrichmentErrorCode : null,
                Assessment: pasted ? Assessment(uj.UserEnrichment) : null,
                history,
                new UserJobDocumentsDto(analyses.GetValueOrDefault(uj.Id), documents.GetValueOrDefault((uj.Id, DocumentKinds.Resume)),
                    documents.GetValueOrDefault((uj.Id, DocumentKinds.CoverLetter)), preps.GetValueOrDefault(uj.Id)),
                Token(uj), uj.CreatedAt, uj.UpdatedAt);
        }).ToList();
    }

    private static UserJobAssessmentDto? Assessment(string? userEnrichment)
    {
        if (ScoredJobs.ParseEnrichment(userEnrichment) is not { } data) return null;
        var e = data.Enrichment;
        return new UserJobAssessmentDto(e.Eligibility, e.EligibilityEvidence, e.WorkMode, e.Summary,
            data.Fit?.Reasons ?? [], data.Fit?.Confidence, data.Fit?.MatchedSkills ?? [], data.Fit?.MissingSkills ?? []);
    }

    /// <summary>Appends <c>{status, at}</c> to a <c>status_history</c> array.</summary>
    public static string AppendHistory(string historyJson, string status, DateTimeOffset at)
    {
        var list = JsonSerializer.Deserialize<List<StatusChangeDto>>(historyJson, Json) ?? [];
        list.Add(new StatusChangeDto(status, at));
        return JsonSerializer.Serialize(list, Json);
    }
}
