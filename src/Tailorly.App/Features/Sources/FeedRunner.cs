using System.Text.Json;
using Hangfire;
using Microsoft.Extensions.Options;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Sources;

/// <summary>What one source run did; stored as <c>source_health.last_run_stats</c> (counts only, never job text).</summary>
public sealed class RunStats
{
    public int Pages { get; set; }
    public int Fetched { get; set; }
    public int New { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Skipped { get; set; }
    public int Closed { get; set; }
    public int Discovered { get; set; }
    public int Boards { get; set; }
    public int BoardsFailed { get; set; }
    public int BoardsNotFound { get; set; }
    public int Requests { get; set; }

    /// <summary>Why each stream stopped: caught_up, all_known, end, page_cap.</summary>
    public Dictionary<string, string> Stops { get; } = new(StringComparer.Ordinal);

    public void Add(IngestResult r)
    {
        Fetched += r.Received;
        New += r.New;
        Updated += r.Updated;
        Unchanged += r.Unchanged;
        Skipped += r.Invalid;
        Discovered += r.DiscoveredLinks.Count;
    }
}

/// <summary>
/// Path A incremental fetch (PRD §7.1b, SDD §8.0). For each stream, newest first:
/// <code>
/// since = last_success_at ?? now - Fetch:FirstRunBackfillDays
/// walk pages from the top; upsert each page; stop when
///   the page's last (oldest) listing is older than since   → caught_up
///   every listing on the page is already stored             → all_known
///   the stream has no more pages                            → end
///   Fetch:MaxPagesPerRun pages were read                    → page_cap (cursor saved)
/// then, if an earlier run stopped at the page cap, resume from its saved cursor with the remaining page budget.
/// </code>
/// The last (not any) listing decides "older than since" because feeds pin featured jobs to the top of page 1 (Himalayas,
/// seen live). Cursors are saved in <c>source_health.last_cursor</c> as <c>{stream: {cursor, since}}</c>.
/// </summary>
public sealed partial class FeedRunner(JobIngestor ingestor, IOptions<FetchOptions> options, TimeProvider clock,
    IBackgroundJobClient jobs, ILogger<FeedRunner> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Resume(string Cursor, DateTimeOffset Since);

    public async Task<RunStats> RunAsync(JobSource source, SourceHealth health, IFeedSource adapter, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var since = health.LastSuccessAt ?? now.AddDays(-o.FirstRunBackfillDays);
        var saved = ReadCursors(health.LastCursor);
        var next = new Dictionary<string, Resume>(StringComparer.Ordinal);
        var stats = new RunStats();

        foreach (var stream in adapter.Streams)
        {
            var budget = Math.Max(1, o.MaxPagesPerRun);
            var (stop, cursor, used) = await WalkAsync(source, adapter, stream, null, since, budget, stats, ct);
            stats.Stops[stream] = stop;
            if (stop == "page_cap" && cursor is not null)
            {
                // The gap below this page is older than the top; keep the oldest floor so it is still read.
                var floor = saved.TryGetValue(stream, out var old) ? (old.Since < since ? old.Since : since) : since;
                next[stream] = new Resume(cursor, floor);
                continue;
            }
            if (saved.TryGetValue(stream, out var resume) && budget - used > 0)
            {
                var (stop2, cursor2, _) = await WalkAsync(source, adapter, stream, resume.Cursor, resume.Since, budget - used, stats, ct);
                stats.Stops[stream] = stop + "+resume:" + stop2;
                if (stop2 == "page_cap" && cursor2 is not null) next[stream] = resume with { Cursor = cursor2 };
            }
        }

        health.LastCursor = next.Count == 0 ? null : JsonSerializer.Serialize(next, Json);
        return stats;
    }

    private async Task<(string Stop, string? Cursor, int Pages)> WalkAsync(JobSource source, IFeedSource adapter, string stream,
        string? cursor, DateTimeOffset since, int budget, RunStats stats, CancellationToken ct)
    {
        for (var pages = 1; ; pages++)
        {
            var page = await adapter.GetNewestPageAsync(stream, cursor, ct);
            stats.Pages++;
            stats.Skipped += page.SkippedRows;
            if (page.Jobs.Count == 0) return ("end", null, pages);

            var result = await ingestor.IngestAsync(source, page.Jobs, companyId: null, ct);
            stats.Add(result);
            foreach (var linkId in result.DiscoveredLinks)
                jobs.Enqueue<PollBoardJob>(j => j.RunAsync(linkId, CancellationToken.None));

            if (page.Jobs[^1].PostedAt is { } oldest && oldest < since) return ("caught_up", null, pages);
            if (result.AllKnown) return ("all_known", null, pages);
            if (page.NextCursor is null) return ("end", null, pages);
            if (pages >= budget)
            {
                LogPageCap(logger, source.Key, stream, budget);
                return ("page_cap", page.NextCursor, pages);
            }
            cursor = page.NextCursor;
        }
    }

    public static Dictionary<string, Resume> ReadCursors(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Resume>>(json, Json) is { } d
                ? new Dictionary<string, Resume>(d, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal); // an unreadable cursor only costs a re-walk
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Source {SourceKey} stream {Stream} reached the page cap ({Pages}); cursor saved")]
    private static partial void LogPageCap(ILogger logger, string sourceKey, string stream, int pages);
}
