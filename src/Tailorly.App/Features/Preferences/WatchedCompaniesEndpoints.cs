using Hangfire;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tailorly.App.Api;
using Tailorly.App.Features.Sources;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Preferences;

/// <summary>
/// The user's companies to watch (PRD F3 <c>watched_companies</c>, ApiDesign §4.5; Path B feeder 2). A board URL is parsed
/// against the host allowlist and reduced to (platform, token); only the adapter's fixed API URL is ever called, never the
/// URL the user typed (SSRF, SDD §14.2). The link is shared; the watch-list row is the user's. Limit 25 per user.
/// </summary>
public static partial class WatchedCompaniesEndpoints
{
    public const int MaxWatched = 25;

    /// <summary>A board checked this recently with open jobs is not re-checked when another user adds it.</summary>
    private static readonly TimeSpan FreshCheck = TimeSpan.FromHours(24);

    public static void MapWatchedCompaniesEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/watched-companies").WithTags("Preferences").RequireAuthorization(AuthPolicies.User);
        g.MapGet("", List).WithName("ListWatchedCompanies");
        g.MapPost("", Add).WithName("AddWatchedCompany");
        g.MapDelete("/{id:guid}", Remove).WithName("RemoveWatchedCompany");
    }

    private static async Task<Ok<List<WatchedCompanyDto>>> List(AppDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await Query(db).ToListAsync(ct));

    /// <summary>The caller's watch list (the user filter applies to <c>user_watched_companies</c>), optionally one link.</summary>
    private static IQueryable<WatchedCompanyDto> Query(AppDbContext db, Guid? linkId = null) =>
        from w in db.UserWatchedCompanies.AsNoTracking()
        join l in db.CompanyAtsLinks.AsNoTracking() on w.CompanyAtsLinkId equals l.Id
        join c in db.Companies.AsNoTracking() on l.CompanyId equals c.Id
        where linkId == null || l.Id == linkId
        orderby c.Name
        select new WatchedCompanyDto(l.Id, c.Name, l.Platform, l.BoardToken, l.Status, l.OpenJobsCount, l.Origin);

    private static async Task<Results<Created<WatchedCompanyDto>, Ok<WatchedCompanyDto>>> Add(AddWatchedCompanyRequest body,
        AppDbContext db, ICurrentUser current, CompanyLinker linker, BoardVerifier verifier, IBackgroundJobClient jobs,
        TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var board = Parse(body);
        var errors = new FieldErrors();
        var name = errors.Text("name", body.Name, 100, required: false, label: "Name");
        errors.ThrowIfAny();

        var link = await db.CompanyAtsLinks.FirstOrDefaultAsync(l => l.Platform == board.Platform && l.BoardToken == board.BoardToken, ct);
        if (link is not null && await db.UserWatchedCompanies.AnyAsync(w => w.CompanyAtsLinkId == link.Id, ct))
            return TypedResults.Ok(await Query(db, link.Id).FirstAsync(ct));
        if (await db.UserWatchedCompanies.CountAsync(ct) >= MaxWatched)
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.WatchlistLimit, "Watch list is full",
                $"You can watch up to {MaxWatched} companies. Remove one to add another.");
        if (link?.Status == LinkStatus.Blocked) throw BoardNotFound();

        var now = clock.GetUtcNow();
        var fresh = link is { Status: LinkStatus.Resolved, OpenJobsCount: > 0 } && link.LastCheckedAt > now - FreshCheck;
        var created = false;
        if (!fresh)
        {
            BoardCheck check;
            try
            {
                check = await verifier.CheckAsync(board.Platform, board.BoardToken, ct);
            }
            catch (SourceException ex)
            {
                LogUnreachable(loggers.CreateLogger(typeof(WatchedCompaniesEndpoints)), board.Platform, ex.Failure, ex.StatusCode);
                throw new ApiException(StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServiceUnavailable,
                    "Board unavailable", "We could not reach that job board just now. Please try again in a few minutes.");
            }
            if (!check.Exists || check.OpenJobs == 0) throw BoardNotFound();

            (link, created) = await linker.EnsureLinkAsync(board.Platform, board.BoardToken,
                name ?? check.CompanyName ?? board.BoardToken, websiteDomain: null, LinkOrigin.User, ct);
            link.Status = LinkStatus.Resolved;
            link.OpenJobsCount = check.OpenJobs;
            link.LastCheckedAt = now;
            link.FailureCount = 0;
            link.NextRetryAt = null;
        }

        db.UserWatchedCompanies.Add(new UserWatchedCompany { UserId = current.UserId, CompanyAtsLinkId = link!.Id, CreatedAt = now });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return TypedResults.Ok(await Query(db, link.Id).FirstAsync(ct));
        }

        // Watched companies are resolved eagerly (PRD §7.3 step 5): poll a board nobody has polled yet right away.
        var linkId = link.Id;
        if (created || !await db.Jobs.AnyAsync(j => j.CompanyId == link.CompanyId && j.SourceKey == link.Platform, ct))
            jobs.Enqueue<PollBoardJob>(j => j.RunAsync(linkId, CancellationToken.None));

        var dto = await Query(db, linkId).FirstAsync(ct);
        return TypedResults.Created($"/api/v1/watched-companies/{linkId}", dto);
    }

    private static async Task<NoContent> Remove(Guid id, AppDbContext db, CancellationToken ct)
    {
        if (await db.UserWatchedCompanies.Where(w => w.CompanyAtsLinkId == id).ExecuteDeleteAsync(ct) == 0)
            throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Not found", "This company is not on your watch list.");
        return TypedResults.NoContent();
    }

    /// <summary>A board URL on an allowlisted host, or an explicit platform + token. Anything else is INVALID_BOARD_URL.</summary>
    internal static AtsBoardRef Parse(AddWatchedCompanyRequest body)
    {
        if (!string.IsNullOrWhiteSpace(body.BoardUrl))
        {
            if (AtsUrl.TryParse(body.BoardUrl, out var fromUrl)) return fromUrl;
            throw InvalidBoard();
        }
        var token = body.BoardToken?.Trim().ToLowerInvariant();
        if (AtsUrl.IsPlatform(body.Platform) && AtsUrl.IsValidToken(token)) return new AtsBoardRef(body.Platform!, token!, null);
        throw InvalidBoard();
    }

    private static ApiException InvalidBoard() => new(StatusCodes.Status422UnprocessableEntity, ErrorCodes.InvalidBoardUrl,
        "Not a supported job board", "Use a Greenhouse, Lever or Ashby board link such as https://boards.greenhouse.io/company.");

    private static ApiException BoardNotFound() => new(StatusCodes.Status422UnprocessableEntity, ErrorCodes.BoardNotFound,
        "Board not found", "That board does not exist or has no public jobs right now.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Watch-list board check failed: {Platform}, {Failure} (HTTP {StatusCode})")]
    private static partial void LogUnreachable(ILogger logger, string platform, SourceFailure failure, int? statusCode);
}
