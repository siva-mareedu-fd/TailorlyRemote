using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Tailorly.App.Infrastructure.Data;

/// <summary>
/// Idempotent seed data (DatabaseDesign §5). Safe to run on every deploy; existing rows (an admin's changes) are kept. The
/// board seed list (<c>watched_companies.seed.json</c>) is loaded by the admin action, not here.
/// </summary>
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

        // Sources and their compliance flags (DatabaseDesign §5, PRD §7.1a). Himalayas, Jobicy and WWR ship disabled; Lever
        // and Ashby too. terms_reviewed_at is set only for the sources whose terms were reviewed against the provider's own
        // pages on 2026-10-25 and that ship enabled (DECISIONS.md); every other source needs an admin review before enabling.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO job_sources
             (key, display_name, kind, requires_attribution, attribution_text, attribution_url, terms_url, terms_reviewed_at,
              require_source_apply_url, forbid_third_party_resolution, purge_on_expiry, requires_logo_attribution,
              poll_interval_minutes, enabled)
            VALUES
             ('himalayas','Himalayas','feed', true,'Source: Himalayas','https://himalayas.app','https://himalayas.app/api', NULL,
              true, true, true, false, 1440, false),
             ('remoteok','Remote OK','feed', true,'Source: Remote OK','https://remoteok.com','https://remoteok.com/legal', '2026-10-25T00:00:00Z',
              true, false, false, false, 360, true),
             ('jobicy','Jobicy','feed', true,'Source: Jobicy','https://jobicy.com','https://jobicy.com/jobs-rss-feed', NULL,
              true, false, false, false, 360, false),
             ('wwr','We Work Remotely','feed', true,'Source: We Work Remotely','https://weworkremotely.com','https://weworkremotely.com/terms-of-service', NULL,
              true, false, false, false, 360, false),
             ('greenhouse','Greenhouse board','board', false, NULL, NULL,'https://developers.greenhouse.io/job-board.html', '2026-10-25T00:00:00Z',
              false, false, false, false, 360, true),
             ('lever','Lever board','board', false, NULL, NULL,'https://github.com/lever/postings-api', NULL,
              false, false, false, false, 360, false),
             ('ashby','Ashby board','board', false, NULL, NULL,'https://developers.ashbyhq.com/docs/public-job-posting-api', NULL,
              false, false, false, false, 360, false),
             ('byo','Added by you','user', false, NULL, NULL, NULL, NULL,
              false, false, false, false, 1440, true)
            ON CONFLICT (key) DO NOTHING;
            INSERT INTO source_health (source_key) SELECT key FROM job_sources ON CONFLICT (source_key) DO NOTHING;
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
