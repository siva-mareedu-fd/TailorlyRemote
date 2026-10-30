using Hangfire;
using Microsoft.Extensions.Options;
using Tailorly.App.Infrastructure.Data;

namespace Tailorly.App.Features.Digests;

/// <summary>
/// <c>DigestDispatcher</c> (every 5 minutes, SDD §11.1): finds users whose local schedule time has passed today (within
/// the 4-hour window) and who have no scheduled digest for that local date, claims one by
/// <c>INSERT … ON CONFLICT DO NOTHING RETURNING</c>, and enqueues <see cref="PerUserRunJob"/> only for rows it claimed.
/// Two ticks or two app instances therefore create exactly one digest per user per local day; the unique index
/// <c>ux_digest_scheduled_per_day</c> is the guarantee, <c>DisableConcurrentExecution</c> only saves work.
/// </summary>
public sealed partial class DigestDispatcherJob(SystemQueries system, IBackgroundJobClient jobs, IOptions<DigestOptions> options,
    TimeProvider clock, ILogger<DigestDispatcherJob> logger)
{
    public const string RecurringId = "digest-dispatcher";

    [DisableConcurrentExecution(timeoutInSeconds: 240)]
    [AutomaticRetry(Attempts = 0)]
    public Task<IReadOnlyList<Guid>> RunAsync(CancellationToken ct) => DispatchAsync(clock.GetUtcNow(), ct);

    /// <summary>One dispatcher tick as of <paramref name="now"/>; returns the ids of the digests this call claimed.</summary>
    public async Task<IReadOnlyList<Guid>> DispatchAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = await system.DueScheduledDigestsAsync(now, Math.Max(1, options.Value.WindowHours), ct);
        var claimed = new List<Guid>();
        foreach (var (userId, localDate) in due)
        {
            var digestId = Guid.CreateVersion7();
            if (!await system.ClaimScheduledDigestAsync(digestId, userId, localDate, ct)) continue;
            jobs.Enqueue<PerUserRunJob>(j => j.RunAsync(digestId, userId, CancellationToken.None));
            claimed.Add(digestId);
        }
        if (due.Count > 0) LogDispatched(logger, due.Count, claimed.Count);
        return claimed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Digest dispatcher: {Due} due, {Claimed} claimed and enqueued")]
    private static partial void LogDispatched(ILogger logger, int due, int claimed);
}
