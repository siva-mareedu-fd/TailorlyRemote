using Hangfire;
using Tailorly.App.Features.Accounts;
using Tailorly.App.Features.Digests;
using Tailorly.App.Features.Resumes;
using Tailorly.App.Features.Sources;

namespace Tailorly.App.Infrastructure.Jobs;

/// <summary>
/// Registers the recurring jobs of PRD §9.4 at startup (only when this process runs the Hangfire server).
/// M1: <c>SweepStuckJobs</c> every 15 minutes and <c>PurgeDeletedAccounts</c> hourly. M2: <c>FetchSources</c> every 10
/// minutes (it enqueues each source when its own <c>poll_interval_minutes</c> is due), the expiry purge hourly and the
/// retention sweep daily at 03:00 IST. M3: <c>DigestDispatcher</c> every 5 minutes.
/// </summary>
public sealed class RecurringJobs(IRecurringJobManager manager) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        manager.AddOrUpdate<SweepStuckWorkJob>(SweepStuckWorkJob.RecurringId, j => j.RunAsync(CancellationToken.None), "*/15 * * * *");
        manager.AddOrUpdate<PurgeDeletedAccountsJob>(PurgeDeletedAccountsJob.RecurringId, j => j.RunAsync(CancellationToken.None), "0 * * * *");
        manager.AddOrUpdate<FetchSourcesJob>(FetchSourcesJob.RecurringId, j => j.RunAsync(CancellationToken.None), "*/10 * * * *");
        manager.AddOrUpdate<RetentionJob>(RetentionJob.ExpiryRecurringId, j => j.PurgeExpiredAsync(CancellationToken.None), "20 * * * *");
        manager.AddOrUpdate<DigestDispatcherJob>(DigestDispatcherJob.RecurringId, j => j.RunAsync(CancellationToken.None), "*/5 * * * *");
        manager.AddOrUpdate<RetentionJob>(RetentionJob.DailyRecurringId, j => j.RunDailyAsync(CancellationToken.None), "30 21 * * *");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
