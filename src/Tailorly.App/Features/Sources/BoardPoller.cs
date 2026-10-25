using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tailorly.App.Features.Sources.Adapters;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Sources;

/// <summary>
/// Path B (PRD §7.1b, SDD §8.0): polls every resolved board of one platform, plus <c>not_found</c> boards whose weekly
/// retry is due. Per board: list without descriptions → fetch details only for ids not yet stored (or the content list
/// once when there are many) → listings no longer on the board are closed. Each board runs in its own try/catch; a
/// 404/410 marks the link <c>not_found</c> and never throws; a 429/403 stops this platform's cycle and is rethrown so the
/// source circuit opens.
/// </summary>
public sealed partial class BoardPoller(AppDbContext db, JobIngestor ingestor, DedupeResolver dedupe, IOptions<FetchOptions> options,
    TimeProvider clock, ILogger<BoardPoller> logger)
{
    public static readonly TimeSpan NotFoundRetry = TimeSpan.FromDays(7);

    public async Task<RunStats> RunAsync(JobSource source, IBoardSource adapter, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var links = await db.CompanyAtsLinks.AsNoTracking()
            .Where(l => l.Platform == source.Key
                        && (l.Status == LinkStatus.Resolved || l.Status == LinkStatus.NotFound)
                        && (l.NextRetryAt == null || l.NextRetryAt <= now))
            .OrderBy(l => l.LastCheckedAt).ThenBy(l => l.Id)
            .Select(l => l.Id)
            .ToListAsync(ct);

        var stats = new RunStats();
        foreach (var linkId in links)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await PollAsync(source, adapter, linkId, stats, ct);
            if (outcome is { Failure: SourceFailure.Throttled }) throw outcome; // open the circuit; other boards wait for the next run
        }
        if (stats.Boards > 0 && stats.BoardsFailed == stats.Boards)
            throw new SourceException(SourceFailure.Unavailable, $"All {stats.Boards} boards failed");
        return stats;
    }

    /// <summary>Polls one board. Returns the failure (already recorded on the link) instead of throwing.</summary>
    public async Task<SourceException?> PollAsync(JobSource source, IBoardSource adapter, Guid linkId, RunStats stats, CancellationToken ct)
    {
        var link = await db.CompanyAtsLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null || link.Status == LinkStatus.Blocked) return null;
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == link.CompanyId, ct);
        stats.Boards++;
        try
        {
            var listing = await adapter.ListAsync(link.BoardToken, withContent: false, ct);
            stats.Skipped += listing.SkippedRows;
            var current = listing.Jobs.Select(j => FillCompany(j, company.Name)).ToList();
            var currentIds = current.Select(j => j.ExternalId).ToHashSet(StringComparer.Ordinal);

            var stored = await db.Jobs.AsNoTracking()
                .Where(j => j.SourceKey == source.Key && j.ExternalId.StartsWith(link.BoardToken + ":"))
                .Select(j => new { j.ExternalId, j.ClosedAt, j.DedupeKey })
                .ToListAsync(ct);
            var storedIds = stored.Select(s => s.ExternalId).ToHashSet(StringComparer.Ordinal);

            List<RawJob> toIngest;
            if (listing.HasDescriptions)
            {
                toIngest = current; // Lever/Ashby lists carry descriptions; unchanged rows are skipped by content hash
            }
            else
            {
                var fresh = current.Where(j => !storedIds.Contains(j.ExternalId)).ToList();
                if (fresh.Count > options.Value.BoardBulkDetailThreshold)
                {
                    var full = await adapter.ListAsync(link.BoardToken, withContent: true, ct);
                    var wanted = fresh.Select(f => f.ExternalId).ToHashSet(StringComparer.Ordinal);
                    toIngest = full.Jobs.Where(j => wanted.Contains(j.ExternalId)).Select(j => FillCompany(j, company.Name)).ToList();
                }
                else
                {
                    toIngest = [];
                    foreach (var f in fresh)
                        if (await adapter.GetJobAsync(link.BoardToken, f.ExternalId, ct) is { } detail)
                            toIngest.Add(FillCompany(detail, company.Name));
                }

                // Listings already stored and still open on the board: re-open if they had been closed.
                var reopened = stored.Where(s => s.ClosedAt != null && currentIds.Contains(s.ExternalId)).Select(s => s.ExternalId).ToList();
                if (reopened.Count > 0)
                {
                    await db.Jobs.Where(j => j.SourceKey == source.Key && reopened.Contains(j.ExternalId))
                        .ExecuteUpdateAsync(s => s.SetProperty(j => j.ClosedAt, (DateTimeOffset?)null), ct);
                    await dedupe.ResolveAsync(stored.Where(s => reopened.Contains(s.ExternalId)).Select(s => s.DedupeKey).ToHashSet(), ct);
                }
            }

            stats.Add(await ingestor.IngestAsync(source, toIngest, link.CompanyId, ct));
            stats.Closed += await CloseMissingAsync(source.Key, link.BoardToken, stored.Where(s => s.ClosedAt == null && !currentIds.Contains(s.ExternalId))
                .Select(s => (s.ExternalId, s.DedupeKey)).ToList(), ct);

            link.Status = LinkStatus.Resolved;
            link.FailureCount = 0;
            link.OpenJobsCount = current.Count;
            link.LastCheckedAt = clock.GetUtcNow();
            link.NextRetryAt = null;
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Any failure stays with this board (PRD §7.2); unexpected ones are reported by type only.
            var failure = ex as SourceException ?? new SourceException(SourceFailure.Unavailable, "Unexpected " + ex.GetType().Name, inner: ex);
            if (ex is not SourceException) LogUnexpected(logger, ex, source.Key, linkId);
            db.ChangeTracker.Clear();
            await RecordFailureAsync(source.Key, linkId, link.BoardToken, failure, stats, ct);
            return failure;
        }
    }

    private async Task RecordFailureAsync(string sourceKey, Guid linkId, string token, SourceException ex, RunStats stats, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var notFound = ex.Failure == SourceFailure.NotFound;
        if (notFound) stats.BoardsNotFound++;
        else stats.BoardsFailed++;
        LogBoardFailed(logger, sourceKey, linkId, ex.Failure, ex.StatusCode);

        await db.CompanyAtsLinks.Where(l => l.Id == linkId).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.FailureCount, l => l.FailureCount + 1)
            .SetProperty(l => l.LastCheckedAt, now)
            .SetProperty(l => l.Status, l => notFound ? LinkStatus.NotFound : l.Status)
            .SetProperty(l => l.NextRetryAt, l => notFound ? now + NotFoundRetry : l.NextRetryAt), ct);

        if (notFound)
        {
            // A board that is gone has no open jobs: close its listings so they stop appearing.
            var open = await db.Jobs.AsNoTracking()
                .Where(j => j.SourceKey == sourceKey && j.ExternalId.StartsWith(token + ":") && j.ClosedAt == null)
                .Select(j => new { j.ExternalId, j.DedupeKey }).ToListAsync(ct);
            stats.Closed += await CloseMissingAsync(sourceKey, token, open.Select(j => (j.ExternalId, j.DedupeKey)).ToList(), ct);
        }
    }

    private async Task<int> CloseMissingAsync(string sourceKey, string token, IReadOnlyList<(string ExternalId, string DedupeKey)> missing,
        CancellationToken ct)
    {
        if (missing.Count == 0) return 0;
        var ids = missing.Select(m => m.ExternalId).Where(id => BoardIds.BelongsTo(token, id)).ToList();
        var now = clock.GetUtcNow();
        var closed = await db.Jobs.Where(j => j.SourceKey == sourceKey && ids.Contains(j.ExternalId) && j.ClosedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ClosedAt, now).SetProperty(j => j.DuplicateOfJobId, (Guid?)null), ct);
        await dedupe.ResolveAsync(missing.Select(m => m.DedupeKey).ToHashSet(StringComparer.Ordinal), ct);
        return closed;
    }

    private static RawJob FillCompany(RawJob job, string companyName) =>
        string.IsNullOrWhiteSpace(job.CompanyName) ? job with { CompanyName = companyName } : job;

    [LoggerMessage(Level = LogLevel.Error, Message = "Board poll crashed: source {SourceKey}, link {LinkId}")]
    private static partial void LogUnexpected(ILogger logger, Exception ex, string sourceKey, Guid linkId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Board poll failed: source {SourceKey}, link {LinkId}, {Failure} (HTTP {StatusCode})")]
    private static partial void LogBoardFailed(ILogger logger, string sourceKey, Guid linkId, SourceFailure failure, int? statusCode);
}
