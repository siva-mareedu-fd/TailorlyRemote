using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
        // Create the history table up front; otherwise EF logs an error for it on an empty database.
        await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(ct);
        await db.Database.MigrateAsync(ct);
        LogMigrated(logger, (await db.Database.GetAppliedMigrationsAsync(ct)).Count());

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Database schema up to date ({AppliedCount} migration(s) applied)")]
    private static partial void LogMigrated(ILogger logger, int appliedCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed data applied")]
    private static partial void LogSeeded(ILogger logger);
}
