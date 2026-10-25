using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Tailorly.App.Infrastructure.Data;

/// <summary>Idempotent seed data (DatabaseDesign §5). Safe to run on every deploy.</summary>
public sealed partial class Seeder(AppDbContext db, RoleManager<AppRole> roles, UserManager<AppUser> users,
    IConfiguration config, ILogger<Seeder> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        // Model ids live only in this table (CLAUDE.md). Verify names at build time.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO ai_task_config (task, model, max_tokens, temperature, bucket) VALUES
             ('resume_parse',        'claude-sonnet-5-5', 4000, 0.0, 'documents'),
             ('job_enrichment',      'claude-haiku-5-5',  1500, 0.0, 'platform'),
             ('byo_job_enrichment',  'claude-haiku-5-5',  1500, 0.0, 'documents'),
             ('job_fit_score',       'claude-haiku-5-5',   600, 0.0, 'discovery'),
             ('ats_analysis',        'claude-sonnet-5-5', 3000, 0.0, 'documents'),
             ('resume_tailor',       'claude-sonnet-5-5', 4000, 0.2, 'documents'),
             ('cover_letter',        'claude-sonnet-5-5', 1200, 0.4, 'documents'),
             ('interview_prep',      'claude-sonnet-5-5', 3000, 0.3, 'documents')
            ON CONFLICT (task) DO NOTHING;
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO feature_flags (key, value) VALUES
             ('signup.require_invite_code', 'false'),
             ('digest.email_enabled',       'true'),
             ('pdf_export.enabled',         'false')
            ON CONFLICT (key) DO NOTHING;
            """, ct);

        if (!await roles.RoleExistsAsync(AppRole.Admin))
        {
            var result = await roles.CreateAsync(new AppRole(AppRole.Admin));
            if (!result.Succeeded) throw new InvalidOperationException("Could not create the Admin role.");
        }

        // Optional: promote an existing account to Admin (Admin__BootstrapEmail). The email itself is never logged.
        var bootstrap = config["Admin:BootstrapEmail"];
        if (!string.IsNullOrWhiteSpace(bootstrap))
        {
            var user = await users.FindByEmailAsync(bootstrap);
            if (user is null)
                LogBootstrapPending(logger);
            else if (!await users.IsInRoleAsync(user, AppRole.Admin))
            {
                await users.AddToRoleAsync(user, AppRole.Admin);
                LogBootstrapDone(logger, user.Id);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin bootstrap account not registered yet; skipping")]
    private static partial void LogBootstrapPending(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Granted Admin role to user {UserId}")]
    private static partial void LogBootstrapDone(ILogger logger, Guid userId);
}
