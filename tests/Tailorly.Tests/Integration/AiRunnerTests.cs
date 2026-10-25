using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Ai;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Budgets;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>The AI runner and budget service against PostgreSQL (SDD §9.1–9.2).</summary>
[Collection(PostgresCollection.Name)]
public sealed class AiRunnerTests(PostgresAppFixture fx)
{
    private TailorlyAppFactory F => fx.Factory;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly AiTaskInput Input = new([new UntrustedBlock("resume_data", "Engineer at Example Corp since 2021.")]);

    private async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = F.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    private async Task<Guid> NewUserIdAsync() => (await TestUsers.CreateVerifiedAsync(F, "runner")).Id;

    [Fact]
    public async Task A_successful_call_returns_typed_output_and_charges_and_records_actual_usage()
    {
        var userId = await NewUserIdAsync();
        var result = await InScope(sp => sp.GetRequiredService<IAiTaskRunner>().RunAsync(ResumeParseTask.Definition, Input, userId, Ct));

        result.Value.Experience.Should().NotBeEmpty();
        result.Model.Should().Be("claude-sonnet-5-5");
        result.PromptVersion.Should().StartWith("resume_parse/");
        result.CountedTokens.Should().BeGreaterThan(0);

        var used = await InScope(sp => sp.GetRequiredService<BudgetService>().GetAsync(userId, BudgetBuckets.Documents, Ct));
        used.Used.Should().Be(result.CountedTokens);
        (await Db.StringsAsync(fx.ConnectionString,
                "SELECT task || ' ' || model || ' ' || bucket || ' ' || prompt_version || ' ' || success FROM ai_usage WHERE user_id = @u", ("u", userId)))
            .Should().Equal($"resume_parse claude-sonnet-5-5 documents {result.PromptVersion} true");
    }

    [Fact]
    public async Task No_call_is_made_when_the_bucket_is_exhausted()
    {
        var userId = await NewUserIdAsync();
        await Db.SetUsedAsync(fx.ConnectionString, userId, "documents", 1_400_000, 1_400_000);
        var before = F.Ai.Requests.Count;

        var act = () => InScope(sp => sp.GetRequiredService<IAiTaskRunner>().RunAsync(ResumeParseTask.Definition, Input, userId, Ct));
        var ex = (await act.Should().ThrowAsync<BudgetExceededException>()).Which;
        ex.Bucket.Should().Be("documents");
        ex.ResetsOn.Day.Should().Be(1);
        F.Ai.Requests.Count.Should().Be(before);
    }

    [Fact]
    public async Task Model_ids_come_from_ai_task_config_and_disabled_tasks_do_not_run()
    {
        var userId = await NewUserIdAsync();
        await Db.ExecAsync(fx.ConnectionString, "UPDATE ai_task_config SET model = 'claude-opus-5-5' WHERE task = 'resume_parse'");
        try
        {
            F.Services.GetRequiredService<TaskConfigCache>().Invalidate(AiTaskNames.ResumeParse);
            var r = await InScope(sp => sp.GetRequiredService<IAiTaskRunner>().RunAsync(ResumeParseTask.Definition, Input, userId, Ct));
            r.Model.Should().Be("claude-opus-5-5");

            await Db.ExecAsync(fx.ConnectionString, "UPDATE ai_task_config SET enabled = false WHERE task = 'resume_parse'");
            F.Services.GetRequiredService<TaskConfigCache>().Invalidate(AiTaskNames.ResumeParse);
            var act = () => InScope(sp => sp.GetRequiredService<IAiTaskRunner>().RunAsync(ResumeParseTask.Definition, Input, userId, Ct));
            (await act.Should().ThrowAsync<AiTaskFailedException>()).Which.Code.Should().Be(ErrorCodes.AiUnavailable);
        }
        finally
        {
            await Db.ExecAsync(fx.ConnectionString, "UPDATE ai_task_config SET model = 'claude-sonnet-5-5', enabled = true WHERE task = 'resume_parse'");
            F.Services.GetRequiredService<TaskConfigCache>().Invalidate(AiTaskNames.ResumeParse);
        }
    }

    [Fact]
    public async Task Calls_without_a_user_are_not_charged_to_any_budget()
    {
        var result = await InScope(sp => sp.GetRequiredService<IAiTaskRunner>().RunAsync(ResumeParseTask.Definition, Input, null, Ct));
        result.CountedTokens.Should().BeGreaterThan(0);
        (await Db.ScalarAsync<long>(fx.ConnectionString, "SELECT count(*) FROM ai_usage WHERE user_id IS NULL AND task = 'resume_parse'"))
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Counters_are_created_lazily_per_period_and_increments_are_atomic()
    {
        var userId = await NewUserIdAsync();
        await Parallel.ForEachAsync(Enumerable.Range(0, 25), Ct, async (_, ct) =>
        {
            await using var scope = F.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BudgetService>().AddAsync(userId, BudgetBuckets.Discovery, 1000, ct);
        });

        var snapshot = await InScope(sp => sp.GetRequiredService<BudgetService>().GetAsync(userId, BudgetBuckets.Discovery, Ct));
        snapshot.Used.Should().Be(25_000);
        snapshot.Limit.Should().Be(600_000);

        // Another period is a separate row with a fresh limit (no reset job).
        await Db.ExecAsync(fx.ConnectionString,
            "INSERT INTO user_budget_counters (user_id, period_key, bucket, limit_tokens, used_tokens) VALUES (@u, '2020-01', 'discovery', 600000, 599999)",
            ("u", userId));
        (await InScope(sp => sp.GetRequiredService<BudgetService>().GetAsync(userId, BudgetBuckets.Discovery, Ct))).Used.Should().Be(25_000);
    }

    [Fact]
    public async Task Budget_counters_are_user_owned_and_filtered()
    {
        var a = await NewUserIdAsync();
        var b = await NewUserIdAsync();
        await Db.SetUsedAsync(fx.ConnectionString, a, "documents", 10, 1_400_000);
        (await Db.WithContextAsync(F, b, db => Task.FromResult(db.UserBudgetCounters.Count()))).Should().Be(0);
        (await Db.WithContextAsync(F, a, db => Task.FromResult(db.UserBudgetCounters.Count()))).Should().Be(1);
    }
}
