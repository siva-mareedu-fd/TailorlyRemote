using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class MigrationTests(PostgresAppFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_migration_creates_account_tables_and_hangfire_schema()
    {
        var tables = await QueryStrings(
            "SELECT table_schema || '.' || table_name FROM information_schema.tables WHERE table_schema IN ('public','hangfire')");

        tables.Should().Contain([
            "public.asp_net_users", "public.asp_net_roles", "public.asp_net_user_roles",
            "public.user_profiles", "public.invite_codes", "public.feature_flags", "public.audit_log",
            "public.ai_task_config", "public.__EFMigrationsHistory",
        ]);
        tables.Should().Contain(t => t.StartsWith("hangfire.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Seed_inserts_ai_task_config_flags_and_admin_role_idempotently()
    {
        await using (var scope = fx.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MigrationRunner>().SeedAsync(Ct); // second run

        await using var s = fx.Factory.Services.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();

        var tasks = await db.AiTaskConfig.AsNoTracking().ToListAsync(Ct);
        tasks.Select(t => t.Task).Should().BeEquivalentTo(
            "resume_parse", "job_enrichment", "byo_job_enrichment", "job_fit_score",
            "ats_analysis", "resume_tailor", "cover_letter", "interview_prep");
        tasks.Single(t => t.Task == "job_enrichment").Bucket.Should().Be("platform");
        tasks.Single(t => t.Task == "resume_tailor").Model.Should().Be("claude-sonnet-5-5");

        var flags = await db.FeatureFlags.AsNoTracking().ToDictionaryAsync(f => f.Key, f => f.Value, Ct);
        flags.Should().HaveCount(3);
        flags["signup.require_invite_code"].Should().Be("false");

        (await db.Roles.CountAsync(r => r.Name == AppRole.Admin, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Check_constraints_are_enforced()
    {
        var act = () => Exec("INSERT INTO ai_task_config (task, model, max_tokens, bucket) VALUES ('x','m',0,'documents')");
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task User_owned_entities_are_filtered_to_nobody_when_unauthenticated()
    {
        await using var s = fx.Factory.Services.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = new AppUser { UserName = "filter-test", Email = "filter-test@example.test" };
        db.Users.Add(user);
        db.UserProfiles.Add(new UserProfile { UserId = user.Id, DisplayName = "F", NotificationEmail = "filter-test@example.test" });
        await db.SaveChangesAsync(Ct);

        (await db.UserProfiles.CountAsync(Ct)).Should().Be(0, "no current user means Guid.Empty, which owns nothing");
        using (CurrentUser.ActAs(user.Id))
            (await db.UserProfiles.CountAsync(Ct)).Should().Be(1);
        using (CurrentUser.ActAs(Guid.NewGuid()))
            (await db.UserProfiles.CountAsync(Ct)).Should().Be(0);
    }

    private async Task<List<string>> QueryStrings(string sql)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        var list = new List<string>();
        while (await r.ReadAsync(Ct)) list.Add(r.GetString(0));
        return list;
    }

    private async Task Exec(string sql)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}
