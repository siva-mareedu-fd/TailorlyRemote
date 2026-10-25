using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Tailorly.App.Infrastructure.Data;

/// <summary>
/// Implements <c>Tailorly.App --migrate</c> and <c>--seed</c>: applies EF migrations, installs the Hangfire schema
/// and runs the idempotent seed. The app never migrates itself on normal startup (SDD §12.5).
/// </summary>
public sealed partial class MigrationRunner(AppDbContext db, Seeder seeder, IConfiguration config, ILogger<MigrationRunner> logger)
{
    public const string HangfireSchema = "hangfire";

    public async Task MigrateAsync(CancellationToken ct)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
        LogMigrating(logger, pending);
        await db.Database.MigrateAsync(ct);

        await using (var conn = new NpgsqlConnection(config.GetConnectionString("Default")))
        {
            await conn.OpenAsync(ct);
            PostgreSqlObjectsInstaller.Install(conn, HangfireSchema);
        }

        await SeedAsync(ct);
    }

    public async Task SeedAsync(CancellationToken ct)
    {
        await seeder.RunAsync(ct);
        LogSeeded(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {PendingCount} pending migration(s)")]
    private static partial void LogMigrating(ILogger logger, int pendingCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed data applied")]
    private static partial void LogSeeded(ILogger logger);
}
