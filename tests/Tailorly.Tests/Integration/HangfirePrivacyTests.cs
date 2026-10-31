using FluentAssertions;
using Hangfire;
using Hangfire.AspNetCore;
using Hangfire.Client;
using Hangfire.Server;
using Job = Hangfire.Common.Job;
using Hangfire.Logging;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Hangfire.States;
using Serilog;
using Serilog.Extensions.Logging;
using MigrationRunner = Tailorly.App.Infrastructure.Data.MigrationRunner;
using Tailorly.App.Infrastructure.Jobs;
using Tailorly.App.Infrastructure.Logging;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>
/// When a job throws, Hangfire logs the exception, stores its message and details in <c>hangfire.state</c> and copies the
/// message into the retry reason; none of that passes through our own log calls. Runs a real Hangfire server on the test
/// database with the app's global job filters and logging pipeline, fails a job with a library exception that echoes a
/// sentinel, and scans the captured logs and every row of the job.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HangfirePrivacyTests(PostgresAppFixture fx)
{
    private const string Queue = "privacy-test";
    private const string Sentinel = "sentinelhangfirequartz";

    /// <summary>Stands in for a job whose library call fails (args are ids only, as for every real job).</summary>
    public static class FailingJob
    {
        public static void Run(Guid id) => throw FaultyFileStore.EchoingLibraryFailure(Sentinel);
    }

    [Fact]
    public async Task A_failed_jobs_exception_message_is_neither_logged_nor_stored()
    {
        var capture = new CapturingLogSink();
        await using var logger = TailorlyLogging.Configure(new LoggerConfiguration().MinimumLevel.Verbose(), [capture]).CreateLogger();
        using var loggerFactory = new SerilogLoggerFactory(logger);
        LogProvider.SetCurrentLogProvider(new AspNetCoreLogProvider(loggerFactory));
        try
        {
            var options = new PostgreSqlStorageOptions
            {
                SchemaName = MigrationRunner.HangfireSchema,
                PrepareSchemaIfNecessary = false,
                QueuePollInterval = TimeSpan.FromMilliseconds(100),
            };
            var storage = new PostgreSqlStorage(new NpgsqlConnectionFactory(fx.ConnectionString, options), options);
            var filters = new Hangfire.Common.JobFilterCollection();
            foreach (var filter in HangfireSetup.GlobalFilters()) // created after the log provider is set: filters cache loggers
                filters.Add(filter);

            // Not JobActivator.Current: Hangfire.AspNetCore points it at a test app's services, which may be disposed by now.
            var activator = new JobActivator();
            var client = new BackgroundJobClient(storage, filters);
            var jobId = client.Create(Job.FromExpression(() => FailingJob.Run(Guid.NewGuid())), new EnqueuedState(Queue));
            try
            {
                // The only overload that takes a filter provider; the others read Hangfire's process-wide filters, which every
                // test app instance appends to.
#pragma warning disable CS0618
                using (new BackgroundJobServer(
                           new BackgroundJobServerOptions { Queues = [Queue], WorkerCount = 1, ServerName = "privacy-test" },
                           storage, [], filters, activator, new BackgroundJobFactory(filters),
                           new BackgroundJobPerformer(filters, activator), new BackgroundJobStateChanger(filters)))
#pragma warning restore CS0618
                {
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    while (CurrentState(storage, jobId) is var state && state != ScheduledState.StateName && state != FailedState.StateName)
                    {
                        DateTime.UtcNow.Should().BeBefore(deadline, "the job should fail within a minute");
                        await Task.Delay(100, TestContext.Current.CancellationToken);
                    }
                }

                var rows = await PersistedText.HangfireJobAsync(fx.ConnectionString, long.Parse(jobId, System.Globalization.CultureInfo.InvariantCulture));
                rows.Should().Contain(r => r.Contains("Retry attempt 1 of 3", StringComparison.Ordinal), "AutomaticRetry scheduled a retry");
                rows.Should().Contain(r => r.Contains("System.Xml.XmlException", StringComparison.Ordinal),
                    "the failure is stored with its exception types and stack");
                rows.Where(r => r.Contains(Sentinel, StringComparison.OrdinalIgnoreCase)).Should()
                    .BeEmpty("a library's exception message must not be stored in Hangfire's tables");

                var logs = capture.Lines.ToList();
                logs.Should().Contain(l => l.Contains("Failed to process the job", StringComparison.Ordinal) && l.Contains(jobId, StringComparison.Ordinal),
                    "Hangfire logs the failure through the app's pipeline");
                logs.Where(l => l.Contains(Sentinel, StringComparison.OrdinalIgnoreCase)).Should()
                    .BeEmpty("a library's exception message must not be logged");
            }
            finally
            {
                client.Delete(jobId);
            }
        }
        finally
        {
            LogProvider.SetCurrentLogProvider(null);
        }
    }

    private static string? CurrentState(JobStorage storage, string jobId)
    {
        using var connection = storage.GetConnection();
        return connection.GetStateData(jobId)?.Name;
    }
}
