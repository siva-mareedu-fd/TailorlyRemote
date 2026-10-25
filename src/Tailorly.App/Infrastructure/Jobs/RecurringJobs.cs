using Hangfire;
using Tailorly.App.Features.Accounts;
using Tailorly.App.Features.Resumes;

namespace Tailorly.App.Infrastructure.Jobs;

/// <summary>
/// Registers the recurring jobs of PRD §9.4 at startup (only when this process runs the Hangfire server).
/// M1: <c>SweepStuckJobs</c> every 15 minutes and <c>PurgeDeletedAccounts</c> hourly.
/// </summary>
public sealed class RecurringJobs(IRecurringJobManager manager) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        manager.AddOrUpdate<SweepStuckWorkJob>(SweepStuckWorkJob.RecurringId, j => j.RunAsync(CancellationToken.None), "*/15 * * * *");
        manager.AddOrUpdate<PurgeDeletedAccountsJob>(PurgeDeletedAccountsJob.RecurringId, j => j.RunAsync(CancellationToken.None), "0 * * * *");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
