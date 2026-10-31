using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tailorly.App.Api;
using Tailorly.App.Features.Jobs;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;

namespace Tailorly.App.Features.Tailoring;

/// <summary>
/// The part of the tracker (ApiDesign §4.7) that tailoring needs in M4: tracking a scored job, reading an entry, pasting a
/// description for a job whose text is limited or gone, and listing an entry's documents. The board, status changes,
/// notes and deletion arrive with the tracker in M5.
/// </summary>
public static class UserJobEndpoints
{
    public const int MaxDescriptionChars = 30_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapUserJobEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/user-jobs").WithTags("Tracker").RequireAuthorization(AuthPolicies.User);
        g.MapPost("", Create).WithName("TrackJob");
        g.MapGet("/{id:guid}", Get).WithName("GetUserJob");
        g.MapPut("/{id:guid}/description", PutDescription).WithName("PutUserJobDescription");
        g.MapGet("/{id:guid}/tailored-documents", ListDocuments).WithName("ListTailoredDocuments");
    }

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

    private static async Task<Ok<UserJobDto>> Get(Guid id, AppDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await ToDtoAsync(db, await db.UserJobs.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw TailoringData.NotFound(), ct));

    /// <summary>
    /// Stores a pasted description (for a limited listing or one that is gone) and clears its enrichment, which is made
    /// again on the next AI action. The text is stored, never fetched from anywhere.
    /// </summary>
    private static async Task<Ok<UserJobDto>> PutDescription(Guid id, UserJobDescriptionRequest body, AppDbContext db, TimeProvider clock,
        CancellationToken ct)
    {
        var uj = await db.UserJobs.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw TailoringData.NotFound();
        var errors = new FieldErrors();
        var text = errors.Text("description", body.Description, MaxDescriptionChars, label: "The description");
        errors.ThrowIfAny();
        uj.UserDescription = text;
        uj.UserEnrichment = null;
        uj.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ToDtoAsync(db, uj, ct));
    }

    private static async Task<Ok<List<TailoredDocumentDto>>> ListDocuments(Guid id, AppDbContext db, TailoredDocuments documents,
        CancellationToken ct)
    {
        if (!await db.UserJobs.AnyAsync(u => u.Id == id, ct)) throw TailoringData.NotFound();
        var rows = await db.TailoredDocuments.AsNoTracking().Where(d => d.UserJobId == id).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(await documents.ToDtosAsync(rows, ct));
    }

    public static async Task<UserJobDto> ToDtoAsync(AppDbContext db, UserJob uj, CancellationToken ct)
    {
        var descriptionOnListing = uj.JobId is { } jobId && await db.Jobs.AsNoTracking().AnyAsync(j => j.Id == jobId && j.DescriptionText != null, ct);
        var analysis = await db.AtsAnalyses.AsNoTracking().Where(a => a.UserJobId == uj.Id).OrderByDescending(a => a.UpdatedAt)
            .Select(a => a.Status).FirstOrDefaultAsync(ct);
        var latest = await db.TailoredDocuments.AsNoTracking().Where(d => d.UserJobId == uj.Id)
            .GroupBy(d => d.Kind)
            .Select(g => new { Kind = g.Key, Status = g.OrderByDescending(d => d.CreatedAt).Select(d => d.Status).First() })
            .ToDictionaryAsync(x => x.Kind, x => x.Status, ct);
        var history = JsonSerializer.Deserialize<List<StatusChangeDto>>(uj.StatusHistory, Json) ?? [];
        return new UserJobDto(uj.Id, uj.JobId, uj.IsByo, uj.Title, uj.CompanyName, uj.ApplyUrl,
            uj.SourceKey is null ? null : new UserJobSourceDto(uj.SourceKey, uj.SourceName ?? uj.SourceKey, uj.AttributionUrl),
            uj.Status, uj.Notes, uj.FitScore, uj.AtsBefore, uj.AtsAfter,
            HasUserDescription: uj.UserDescription is not null,
            DescriptionAvailable: uj.UserDescription is not null || descriptionOnListing,
            EnrichmentStatus: uj.UserDescription is null ? null : uj.UserEnrichment is null ? WorkStatus.Queued : WorkStatus.Ready,
            history,
            new UserJobDocumentsDto(analysis, latest.GetValueOrDefault(DocumentKinds.Resume), latest.GetValueOrDefault(DocumentKinds.CoverLetter), null),
            uj.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), uj.CreatedAt, uj.UpdatedAt);
    }

    /// <summary>Appends <c>{status, at}</c> to a <c>status_history</c> array.</summary>
    public static string AppendHistory(string historyJson, string status, DateTimeOffset at)
    {
        var list = JsonSerializer.Deserialize<List<StatusChangeDto>>(historyJson, Json) ?? [];
        list.Add(new StatusChangeDto(status, at));
        return JsonSerializer.Serialize(list, Json);
    }
}
