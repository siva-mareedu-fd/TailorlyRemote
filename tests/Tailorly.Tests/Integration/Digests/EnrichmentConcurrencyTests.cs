using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Ai;
using Tailorly.App.Features.Digests;
using Tailorly.App.Infrastructure.Ai;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.MatchingTestData;

namespace Tailorly.Tests.Integration.Digests;

/// <summary>
/// Runs on the same schedule slot want the same jobs enriched at the same moment (M6 load test). Within one process the
/// first run makes each call and the others reuse its row, so the platform pays once per job.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EnrichmentConcurrencyTests(PostgresAppFixture fx)
{
    private TailorlyAppFactory F => fx.Factory;

    [Fact]
    public async Task Concurrent_runs_enrich_each_job_once()
    {
        F.Ai.ClearScript();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add(await InsertJobAsync(F, $"{NewRole()} Engineer"));
        var reply = JsonSerializer.Serialize(Enrichment());
        for (var i = 0; i < 20; i++) // more than enough for duplicates to show
            F.Ai.Enqueue(AiTaskNames.JobEnrichment, r =>
            {
                Thread.Sleep(300); // slow enough that the runs overlap
                return FakeModelProvider.Reply(r, reply);
            });
        var before = F.Ai.Requests.Count(r => r.Task == AiTaskNames.JobEnrichment);

        async Task<EnrichmentOutcome> RunAsync()
        {
            await using var scope = F.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<EnrichmentService>().EnsureAsync(ids, CancellationToken.None);
        }
        var outcomes = await Task.WhenAll(RunAsync(), RunAsync(), RunAsync());

        (F.Ai.Requests.Count(r => r.Task == AiTaskNames.JobEnrichment) - before).Should().Be(5, "one model call per job");
        outcomes.Sum(o => o.Created).Should().Be(5);
        outcomes.Should().OnlyContain(o => o.Failed == 0 && !o.CeilingHit);
        (await Db.ScalarAsync<long>(fx.ConnectionString, "SELECT count(*) FROM job_enrichment WHERE job_id = ANY(@ids)", ("ids", ids.ToArray())))
            .Should().Be(5);
        F.Ai.ClearScript();
    }
}
