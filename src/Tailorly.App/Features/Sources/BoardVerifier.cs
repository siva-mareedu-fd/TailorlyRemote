using Microsoft.EntityFrameworkCore;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Sources;

/// <param name="Exists">The board answered with a job list (a 404/410 is false).</param>
public sealed record BoardCheck(bool Exists, int OpenJobs, string? CompanyName);

/// <summary>
/// Checks a board through its adapter's fixed URL template (ApiDesign §4.5: "builds the fixed API URL from the validated
/// token"). Used by the watch list, the admin add/verify actions and the seed loader. Never fetches a user-supplied URL.
/// </summary>
public sealed class BoardVerifier(SourceRegistry registry, AppDbContext db, TimeProvider clock)
{
    /// <summary>Throws <see cref="SourceException"/> (other than NotFound) when the board could not be checked.</summary>
    public async Task<BoardCheck> CheckAsync(string platform, string token, CancellationToken ct)
    {
        var adapter = registry.Board(platform) ?? throw new ArgumentException("No adapter for this platform.", nameof(platform));
        try
        {
            var listing = await adapter.ListAsync(AtsUrl.RequireToken(token), withContent: false, ct);
            return new BoardCheck(true, listing.Jobs.Count, listing.CompanyName);
        }
        catch (SourceException ex) when (ex.Failure == SourceFailure.NotFound)
        {
            return new BoardCheck(false, 0, null);
        }
    }

    /// <summary>Checks a stored link and records the result on it (status, open jobs, last checked, failures).</summary>
    public async Task<BoardCheck> VerifyLinkAsync(CompanyAtsLink link, CancellationToken ct)
    {
        var check = await CheckAsync(link.Platform, link.BoardToken, ct);
        var now = clock.GetUtcNow();
        link.LastCheckedAt = now;
        if (check.Exists)
        {
            if (link.Status == LinkStatus.NotFound) link.Status = LinkStatus.Resolved;
            link.OpenJobsCount = check.OpenJobs;
            link.FailureCount = 0;
            link.NextRetryAt = null;
        }
        else
        {
            if (link.Status != LinkStatus.Blocked) link.Status = LinkStatus.NotFound;
            link.OpenJobsCount = 0;
            link.FailureCount++;
            link.NextRetryAt = now + BoardPoller.NotFoundRetry;
        }
        await db.SaveChangesAsync(ct);
        return check;
    }
}
