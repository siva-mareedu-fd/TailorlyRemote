using System.Globalization;
using Hangfire;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Tailorly.App.Api;
using Tailorly.App.Features.Jobs;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Time;

namespace Tailorly.App.Features.Digests;

/// <summary>
/// Digest history and runs (ApiDesign §4.6): <c>GET /digests</c>, <c>GET /digests/{id}</c> (poll here for manual runs) and
/// <c>POST /runs</c> ("Run now": at most 3 per local day, never emails, returns the run in flight with 200).
/// </summary>
public static partial class DigestEndpoints
{
    public const int MaxPageSize = 50;
    private static readonly string[] Kinds = [DigestKinds.Scheduled, DigestKinds.Manual];

    public static void MapDigestEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/digests").WithTags("Digests").RequireAuthorization(AuthPolicies.User);
        g.MapGet("", List).WithName("ListDigests");
        g.MapGet("/{id:guid}", Get).WithName("GetDigest");
        api.MapPost("/runs", StartRun).WithName("StartRun").WithTags("Digests").RequireAuthorization(AuthPolicies.User)
            .RequireRateLimiting(RateLimiting.AiStart);
    }

    private static ApiException NotFound() =>
        new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Not found", "This digest does not exist.");

    /// <param name="kind">scheduled | manual (both when omitted).</param>
    private static async Task<Ok<DigestPage>> List(AppDbContext db, string? kind, int? page, int? pageSize, CancellationToken ct)
    {
        if (kind is not null && !Kinds.Contains(kind, StringComparer.Ordinal))
            throw FieldErrors.Single("kind", "Allowed values: scheduled, manual.");
        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 20, 1, MaxPageSize);
        var query = db.Digests.AsNoTracking().Where(d => kind == null || d.Kind == kind);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(d => d.CreatedAt).Skip((p - 1) * size).Take(size)
            .Select(d => new { Digest = d, Items = db.DigestItems.Count(i => i.DigestId == d.Id) })
            .ToListAsync(ct);
        return TypedResults.Ok(new DigestPage(rows.Select(r => DigestSummaryDto.From(r.Digest, r.Items)).ToList(), p, size, total));
    }

    private static async Task<Ok<DigestDetailDto>> Get(Guid id, AppDbContext db, ScoredJobs scored, ICurrentUser current,
        CancellationToken ct)
    {
        var digest = await db.Digests.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw NotFound();
        return TypedResults.Ok(await DetailAsync(db, scored, digest, current.UserId, ct));
    }

    /// <summary>The digest with its items in rank order, as they were chosen when the run finished.</summary>
    internal static async Task<DigestDetailDto> DetailAsync(AppDbContext db, ScoredJobs scored, Digest digest, Guid userId,
        CancellationToken ct)
    {
        var items = await db.DigestItems.AsNoTracking().Where(i => i.DigestId == digest.Id)
            .Select(i => new { i.ScoreId, i.Rank }).ToListAsync(ct);
        if (items.Count == 0) return DigestDetailDto.From(digest, []);
        var rank = items.ToDictionary(i => i.ScoreId, i => (int)i.Rank);
        var ids = rank.Keys.ToList();
        var rows = await scored.Query(db.UserJobScores.Where(s => ids.Contains(s.Id))).ToListAsync(ct);
        var prefs = await MatchContext.PreferencesAsync(db, userId, ct);
        var views = (await scored.EvaluateAsync(rows, MatchContext.ToMatch(prefs), ct)).OrderBy(v => rank[v.Row.ScoreId]).ToList();
        var jobs = await scored.ToSummariesAsync(views, ct);
        return DigestDetailDto.From(digest, views.Zip(jobs, (v, j) => new DigestItemDto(rank[v.Row.ScoreId], j)).ToList());
    }

    private static async Task<Results<Accepted<DigestDetailDto>, Ok<DigestDetailDto>>> StartRun(AppDbContext db, ScoredJobs scored,
        ICurrentUser current, IBackgroundJobClient jobs, IOptions<DigestOptions> options, TimeProvider clock, ILoggerFactory loggers,
        CancellationToken ct)
    {
        var userId = current.UserId;
        if (!await db.Resumes.AnyAsync(r => r.IsActive && r.ParseStatus == WorkStatus.Ready, ct))
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.ResumeRequired, "Résumé required",
                "Upload a résumé and wait for it to be read before running a search.");

        if (await InFlightAsync(db, ct) is { } inflight)
            return TypedResults.Ok(await DetailAsync(db, scored, inflight, userId, ct));

        var zone = await db.UserProfiles.AsNoTracking().Select(p => p.TimeZone).FirstAsync(ct);
        var tz = TimeZones.RequireIana(TimeZones.Canonicalize(zone));
        var now = clock.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(now, tz);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var limit = Math.Max(1, options.Value.MaxManualRunsPerDay);
        if (await db.Digests.CountAsync(d => d.Kind == DigestKinds.Manual && d.LocalDate == today, ct) >= limit)
        {
            var untilMidnight = localNow.Date.AddDays(1) - localNow.DateTime;
            throw new ApiException(StatusCodes.Status429TooManyRequests, ErrorCodes.RunLimitReached, "Run limit reached",
                    $"You can run the search {limit} times a day. Your daily digest still arrives as scheduled.")
                .WithHeader("Retry-After", ((int)Math.Ceiling(untilMidnight.TotalSeconds)).ToString(CultureInfo.InvariantCulture));
        }

        var digest = new Digest { UserId = userId, Kind = DigestKinds.Manual, LocalDate = today };
        db.Digests.Add(digest);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(digest).State = EntityState.Detached;
            if (await InFlightAsync(db, ct) is { } raced) return TypedResults.Ok(await DetailAsync(db, scored, raced, userId, ct));
            throw;
        }

        var id = digest.Id;
        jobs.Enqueue<PerUserRunJob>(j => j.RunAsync(id, userId, CancellationToken.None));
        LogStarted(loggers.CreateLogger(typeof(DigestEndpoints)), id);
        return TypedResults.Accepted($"/api/v1/digests/{id}", DigestDetailDto.From(digest, []));
    }

    private static Task<Digest?> InFlightAsync(AppDbContext db, CancellationToken ct) =>
        db.Digests.AsNoTracking().FirstOrDefaultAsync(d => d.Kind == DigestKinds.Manual
                                                           && (d.Status == WorkStatus.Queued || d.Status == WorkStatus.Running), ct);

    [LoggerMessage(Level = LogLevel.Information, Message = "Manual run {DigestId} started")]
    private static partial void LogStarted(ILogger logger, Guid digestId);
}
