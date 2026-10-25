using Hangfire;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tailorly.App.Infrastructure.Data;
using Tailorly.App.Infrastructure.Storage;

namespace Tailorly.App.Features.Admin;

/// <summary>
/// <c>GET /admin/coverage</c> (the M2 one-week coverage report) and <c>GET /admin/status</c> (the single operational view,
/// SDD §15.2). Status covers what exists in M2; digest runs join in M3.
/// </summary>
public static class AdminReportsEndpoints
{
    public static void MapAdminReports(this RouteGroupBuilder admin)
    {
        admin.MapGet("/coverage", Coverage).WithName("AdminCoverage");
        admin.MapGet("/status", Status).WithName("AdminStatus");
    }

    private static async Task<Ok<CoverageReportDto>> Coverage(CoverageReport report, DateOnly? from, DateOnly? to, string? roles,
        CancellationToken ct) =>
        TypedResults.Ok(await report.BuildAsync(from, to, CoverageReport.ParseRoles(roles), ct));

    private static async Task<Ok<AdminStatusDto>> Status(AppDbContext db, SystemQueries system, IConfiguration config,
        IOptions<StorageOptions> storage, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var sources = await (from s in db.JobSources.AsNoTracking()
                             join h in db.SourceHealth.AsNoTracking() on s.Key equals h.SourceKey
                             where s.Kind != "user"
                             orderby s.Kind, s.Key
                             select new { s.Key, s.DisplayName, s.Enabled, h }).ToListAsync(ct);

        var jobs = new StatusJobsDto(
            await db.Jobs.CountAsync(ct),
            await db.Jobs.CountAsync(j => j.ClosedAt == null && j.DuplicateOfJobId == null, ct),
            await db.Jobs.CountAsync(j => j.CreatedAt > now.AddHours(-24), ct),
            await db.Jobs.CountAsync(j => j.ClosedAt == null && j.DescriptionCompleteness == Completeness.Limited, ct),
            await db.Jobs.CountAsync(j => j.ClosedAt == null && j.DescriptionCompleteness == Completeness.Full, ct));
        var links = await db.CompanyAtsLinks.AsNoTracking().GroupBy(l => l.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var boards = new StatusBoardsDto(links.GetValueOrDefault(LinkStatus.Resolved), links.GetValueOrDefault(LinkStatus.NotFound),
            links.GetValueOrDefault(LinkStatus.Blocked));

        var ai = await db.AiUsage.AsNoTracking().Where(u => u.CreatedAt > now.AddHours(-24))
            .GroupBy(u => new { u.Task, u.Model })
            .Select(g => new StatusAiRowDto(g.Key.Task, g.Key.Model, g.Count(), g.Sum(x => (long)x.CountedTokens), g.Count(x => !x.Success)))
            .ToListAsync(ct);
        var india = Tailorly.Core.Time.TimeZones.RequireIana(Tailorly.Core.Time.TimeZones.India);
        var localMidnight = TimeZoneInfo.ConvertTime(now, india).Date;
        var dayStart = new DateTimeOffset(localMidnight, india.GetUtcOffset(localMidnight)).ToUniversalTime();
        var enrichment = await db.AiUsage.AsNoTracking()
            .Where(u => u.UserId == null && u.Task == "job_enrichment" && u.CreatedAt >= dayStart)
            .SumAsync(u => (long)u.CountedTokens, ct);

        return TypedResults.Ok(new AdminStatusDto(
            sources.Select(s => new StatusSourceDto(s.Key, s.DisplayName, s.Enabled, AdminEndpoints.Health(s.h))).ToList(),
            jobs, boards, Queue(), await system.StuckResourceCountAsync(now.AddMinutes(-30), ct), ai, enrichment,
            config.GetValue<long?>("Ai:PlatformDailyEnrichmentTokenCeiling"), Disk(storage.Value.RootPath)));
    }

    private static StatusQueueDto? Queue()
    {
        try
        {
            var m = JobStorage.Current.GetMonitoringApi();
            var stats = m.GetStatistics();
            return new StatusQueueDto(stats.Enqueued, stats.Processing, stats.Scheduled, stats.Failed);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.NpgsqlException)
        {
            return null;
        }
    }

    private static StatusDiskDto? Disk(string? root)
    {
        try
        {
            var path = Path.GetFullPath(string.IsNullOrWhiteSpace(root) ? "." : root);
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            var used = drive.TotalSize == 0 ? 0 : (int)Math.Round(100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize);
            return new StatusDiskDto(drive.TotalSize, drive.AvailableFreeSpace, used);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
