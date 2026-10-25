using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Sources;

/// <summary>
/// Loads <c>watched_companies.seed.json</c> (DatabaseDesign §5; embedded in the app) into <c>companies</c> +
/// <c>company_ats_links</c> with <c>origin='seed'</c>. Idempotent: boards already known are skipped. New boards are then
/// verified in the background through the fixed board URL (<see cref="VerifyLinksJob"/>), which records
/// <c>open_jobs_count</c>. Every entry was checked against the live API when the file was built (DECISIONS.md).
/// </summary>
public sealed partial class SeedLoader(CompanyLinker linker, IBackgroundJobClient jobs, ILogger<SeedLoader> logger)
{
    public const string ResourceName = "seed/watched_companies.seed.json";

    public sealed record Entry(string? Name, string? WebsiteDomain, string? Platform, string? BoardToken);

    public static IReadOnlyList<Entry> ReadEmbedded()
    {
        using var stream = typeof(SeedLoader).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        return JsonSerializer.Deserialize<List<Entry>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    }

    public async Task<(int Added, int Skipped)> LoadAsync(CancellationToken ct)
    {
        var added = new List<Guid>();
        var skipped = 0;
        foreach (var e in ReadEmbedded())
        {
            var token = e.BoardToken?.Trim().ToLowerInvariant();
            if (!AtsUrl.IsPlatform(e.Platform) || !AtsUrl.IsValidToken(token) || string.IsNullOrWhiteSpace(e.Name))
            {
                skipped++;
                continue;
            }
            var (link, created) = await linker.EnsureLinkAsync(e.Platform!, token!, e.Name!, e.WebsiteDomain, LinkOrigin.Seed, ct);
            if (created) added.Add(link.Id);
            else skipped++;
        }
        if (added.Count > 0)
        {
            var ids = added.ToArray();
            jobs.Enqueue<VerifyLinksJob>(j => j.RunAsync(ids, CancellationToken.None));
        }
        LogLoaded(logger, added.Count, skipped);
        return (added.Count, skipped);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed boards loaded: {Added} added, {Skipped} skipped")]
    private static partial void LogLoaded(ILogger logger, int added, int skipped);
}

/// <summary>The admin verify tool for many boards at once: each board in its own try/catch; the result goes on the link.</summary>
public sealed partial class VerifyLinksJob(AppDbContext db, BoardVerifier verifier, ILogger<VerifyLinksJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task<int> RunAsync(Guid[] linkIds, CancellationToken ct)
    {
        var verified = 0;
        foreach (var id in linkIds)
        {
            var link = await db.CompanyAtsLinks.FirstOrDefaultAsync(l => l.Id == id, ct);
            if (link is null) continue;
            try
            {
                await verifier.VerifyLinkAsync(link, ct);
                verified++;
            }
            catch (SourceException ex)
            {
                LogFailed(logger, id, ex.Failure, ex.StatusCode);
                db.ChangeTracker.Clear();
            }
        }
        return verified;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Board verify failed: link {LinkId}, {Failure} (HTTP {StatusCode})")]
    private static partial void LogFailed(ILogger logger, Guid linkId, SourceFailure failure, int? statusCode);
}
