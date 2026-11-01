using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Features.Accounts;
using Tailorly.App.Features.Sources;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>
/// Retention of <c>ai_usage</c> (13 months, then a monthly roll-up without the user link) and the restore runbook's
/// "replay the deletion log" step (SDD §12.4, §13.4).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RetentionAndReplayTests(PostgresAppFixture fx)
{
    private TailorlyAppFactory F => fx.Factory;
    private string Cs => fx.ConnectionString;

    [Fact]
    public async Task Ai_usage_older_than_13_months_is_rolled_up_per_month_without_the_user()
    {
        var user = await TestUsers.CreateVerifiedAsync(F, "rollup");
        await Db.ExecAsync(Cs, """
            INSERT INTO ai_usage (user_id, task, model, bucket, period_key, input_tokens, output_tokens, counted_tokens, est_cost_usd, success, created_at)
            VALUES (@u, 'rollup_test', 'm1', 'documents', '2025-08', 100, 10, 110, 0.5, true,  '2025-08-03T10:00:00Z'),
                   (@u, 'rollup_test', 'm1', 'documents', '2025-08', 200, 20, 220, 0.25, true, '2025-08-20T10:00:00Z'),
                   (@u, 'rollup_test', 'm1', 'documents', '2025-08', 50,  0,  50,  NULL, false, '2025-08-21T10:00:00Z'),
                   (@u, 'rollup_test', 'm1', 'documents', '2026-10', 7,   7,  14,  NULL, true, now());
            """, ("u", user.Id));
        try
        {
            await RollUpAsync();
            var rows = await Db.StringsAsync(Cs, """
                SELECT concat_ws('|', coalesce(user_id::text, 'null'), period_key, success, input_tokens, output_tokens, counted_tokens,
                                 coalesce(est_cost_usd::text, 'null'), coalesce(prompt_version, ''), created_at::date)
                FROM ai_usage WHERE task = 'rollup_test' ORDER BY period_key, success
                """);
            rows.Should().Equal(
                "null|2025-08|f|50|0|50|null|monthly-rollup|2025-08-01",
                "null|2025-08|t|300|30|330|0.75000|monthly-rollup|2025-08-01",
                $"{user.Id}|2026-10|t|7|7|14|null||{DateTime.UtcNow:yyyy-MM-dd}");

            await RollUpAsync();
            (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM ai_usage WHERE task = 'rollup_test'")).Should().Be(3, "roll-ups are final");
        }
        finally
        {
            await Db.ExecAsync(Cs, "DELETE FROM ai_usage WHERE task = 'rollup_test'");
        }
    }

    private async Task RollUpAsync()
    {
        await using var scope = F.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RetentionJob>().RollUpAiUsageAsync(DateTimeOffset.UtcNow.AddMonths(-13), CancellationToken.None);
    }

    [Fact]
    public async Task Replaying_the_deletion_log_purges_accounts_that_a_restore_brought_back()
    {
        // An account deleted after the backup was taken: the restored database still has it (and its files), the log lists it.
        var restored = await TestUsers.CreateVerifiedAsync(F, "restored");
        await TestUsers.UploadParsedResumeAsync(F, restored);
        var keep = await TestUsers.CreateVerifiedAsync(F, "kept");
        var storageKey = (await Db.StringsAsync(Cs, "SELECT storage_key FROM resumes WHERE user_id = @u", ("u", restored.Id))).Single();
        File.Exists(Path.Combine(F.StorageRoot, storageKey)).Should().BeTrue();

        var log = Path.Combine(Path.GetTempPath(), $"tailorly-deletions-{Guid.NewGuid():N}.log");
        var gone = Guid.NewGuid(); // deleted and not in this backup either
        await File.WriteAllTextAsync(log, $"{gone:D},2026-10-30T10:00:00Z\n{restored.Id:D},2026-10-31T09:00:00Z\n{restored.Id:D},2026-10-31T09:00:01Z\n",
            TestContext.Current.CancellationToken);
        try
        {
            (int Listed, int Purged) result;
            await using (var scope = F.Services.CreateAsyncScope())
                result = await scope.ServiceProvider.GetRequiredService<DeletionLogReplay>().RunAsync(log, CancellationToken.None);
            result.Should().Be((2, 1));

            (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM asp_net_users WHERE id = @u", ("u", restored.Id))).Should().Be(0);
            (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM resumes WHERE user_id = @u", ("u", restored.Id))).Should().Be(0);
            File.Exists(Path.Combine(F.StorageRoot, storageKey)).Should().BeFalse("the files go first, as in the original purge");
            (await keep.Api.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK, "accounts not in the log are untouched");

            await using (var scope = F.Services.CreateAsyncScope())
                (await scope.ServiceProvider.GetRequiredService<DeletionLogReplay>().RunAsync(log, CancellationToken.None))
                    .Should().Be((2, 0), "replaying twice is harmless");
        }
        finally
        {
            File.Delete(log);
        }
    }
}
