using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Api;
using Tailorly.App.Features.Accounts;
using Tailorly.App.Features.Sources;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Admin;

/// <summary>
/// Admin API for sources and companies (ApiDesign §4.11; PRD F15 source health, company table, "run fetch now"). Admin
/// role only, the <c>admin</c> rate-limit bucket, and every call is written to <c>audit_log</c> (route, ids, outcome; no
/// bodies). Enabling a source whose terms were never reviewed is refused with <c>409 TERMS_NOT_REVIEWED</c>.
/// </summary>
public static partial class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminGroup(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Admin").RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimiting.Admin);
        admin.AddEndpointFilter(AuditAsync);

        admin.MapGet("/sources", ListSources).WithName("AdminListSources");
        admin.MapPut("/sources/{key}", UpdateSource).WithName("AdminUpdateSource");
        admin.MapPost("/sources/{key}/run", RunSource).WithName("AdminRunSource");

        admin.MapGet("/companies", ListCompanies).WithName("AdminListCompanies");
        admin.MapPost("/companies", AddCompany).WithName("AdminAddCompany");
        admin.MapPut("/companies/{linkId:guid}", UpdateCompany).WithName("AdminUpdateCompany");
        admin.MapPost("/companies/{linkId:guid}/verify", VerifyCompany).WithName("AdminVerifyCompany");
        admin.MapPost("/companies/seed/load", LoadSeed).WithName("AdminLoadSeedCompanies");
        return admin;
    }

    /// <summary>Audits every admin call, including refused ones (SDD §14.2 "all admin actions audit-logged").</summary>
    private static async ValueTask<object?> AuditAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        string outcome = "ok";
        try
        {
            return await next(ctx);
        }
        catch (ApiException ex)
        {
            outcome = ex.Code;
            throw;
        }
        catch
        {
            outcome = ErrorCodes.InternalError;
            throw;
        }
        finally
        {
            var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value;
            var id = http.Request.RouteValues.TryGetValue("key", out var k) ? k?.ToString()
                : http.Request.RouteValues.TryGetValue("linkId", out var l) ? l?.ToString() : null;
            var actor = http.RequestServices.GetRequiredService<ICurrentUser>().UserId;
            await http.RequestServices.GetRequiredService<AuditWriter>().WriteAsync(
                "admin." + http.Request.Method.ToLowerInvariant(), actor, route, id, new { outcome }, CancellationToken.None);
        }
    }

    // ---------- sources ----------

    private static async Task<Ok<List<AdminSourceDto>>> ListSources(AppDbContext db, SourceRegistry registry, CancellationToken ct)
    {
        var rows = await (from s in db.JobSources.AsNoTracking()
                          join h in db.SourceHealth.AsNoTracking() on s.Key equals h.SourceKey
                          orderby s.Kind, s.Key
                          select new { s, h }).ToListAsync(ct);
        var open = await db.Jobs.AsNoTracking().Where(j => j.ClosedAt == null && j.DuplicateOfJobId == null)
            .GroupBy(j => j.SourceKey).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return TypedResults.Ok(rows.Select(r => ToDto(r.s, r.h, registry.Get(r.s.Key) is not null, open.GetValueOrDefault(r.s.Key))).ToList());
    }

    internal static AdminSourceDto ToDto(JobSource s, SourceHealth h, bool hasAdapter, int openJobs) => new(
        s.Key, s.DisplayName, s.Kind, s.Enabled, hasAdapter, s.RequiresAttribution, s.AttributionText, s.AttributionUrl, s.TermsUrl,
        s.TermsReviewedAt, s.RequireSourceApplyUrl, s.ForbidThirdPartyResolution, s.PurgeOnExpiry, s.RequiresLogoAttribution,
        s.PollIntervalMinutes, openJobs, Health(h));

    internal static SourceHealthDto Health(SourceHealth h) => new(h.LastSuccessAt, h.LastFailureAt, h.LastError, h.ConsecutiveFailures,
        h.CircuitOpenUntil, h.CallsThisMonth, h.LastRunStats is null ? null : JsonDocument.Parse(h.LastRunStats).RootElement.Clone());

    private static async Task<Ok<AdminSourceDto>> UpdateSource(string key, UpdateSourceRequest body, AppDbContext db,
        SourceRegistry registry, CancellationToken ct)
    {
        var s = await db.JobSources.FirstOrDefaultAsync(x => x.Key == key, ct) ?? throw NotFound("source");
        var errors = new FieldErrors();
        if (body.PollIntervalMinutes is < 5 or > 10_080) errors.Add("pollIntervalMinutes", "Use 5 to 10,080 minutes.");
        var text = errors.Text("attributionText", body.AttributionText, 200, required: false, label: "Attribution text");
        if (body.AttributionUrl is { Length: > 0 } url && !JobNormalizer.IsHttpUrl(url)) errors.Add("attributionUrl", "Use an http(s) URL.");
        errors.ThrowIfAny();

        if (body.ClearTermsReview == true)
        {
            if (s.Enabled && body.Enabled != false) throw TermsNotReviewed("Disable the source before clearing its terms review.");
            s.TermsReviewedAt = null;
        }
        if (body.TermsReviewedAt is { } reviewed) s.TermsReviewedAt = reviewed.ToUniversalTime();
        if (body.Enabled == true && !s.Enabled)
        {
            if (s.TermsReviewedAt is null) throw TermsNotReviewed("Review this source's terms and set the review date before enabling it.");
            s.Enabled = true;
        }
        else if (body.Enabled == false) s.Enabled = false;
        if (body.PollIntervalMinutes is { } poll) s.PollIntervalMinutes = poll;
        if (body.AttributionText is not null) s.AttributionText = text;
        if (body.AttributionUrl is not null) s.AttributionUrl = body.AttributionUrl.Length == 0 ? null : body.AttributionUrl;
        await db.SaveChangesAsync(ct);

        var h = await db.SourceHealth.AsNoTracking().FirstAsync(x => x.SourceKey == key, ct);
        var open = await db.Jobs.CountAsync(j => j.SourceKey == key && j.ClosedAt == null && j.DuplicateOfJobId == null, ct);
        return TypedResults.Ok(ToDto(s, h, registry.Get(key) is not null, open));
    }

    private static async Task<Accepted<RunStartedDto>> RunSource(string key, AppDbContext db, SourceRegistry registry,
        IBackgroundJobClient jobs, CancellationToken ct)
    {
        var s = await db.JobSources.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct) ?? throw NotFound("source");
        if (registry.Get(key) is null) throw FieldErrors.Single("key", "This source has no adapter, so it cannot be fetched.");
        if (!s.Enabled) throw FieldErrors.Single("key", "Enable the source first.");
        var id = jobs.Enqueue<FetchSourceJob>(j => j.RunAsync(key, true, CancellationToken.None));
        return TypedResults.Accepted((string?)null, new RunStartedDto(id));
    }

    // ---------- companies ----------

    private static async Task<Ok<AdminCompanyPage>> ListCompanies(AppDbContext db, SystemQueries system, string? platform,
        string? status, string? origin, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 20, 1, 100);
        var query = from l in db.CompanyAtsLinks.AsNoTracking()
                    join c in db.Companies.AsNoTracking() on l.CompanyId equals c.Id
                    select new { l, c };
        if (!string.IsNullOrWhiteSpace(platform)) query = query.Where(x => x.l.Platform == platform);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.l.Status == status);
        if (!string.IsNullOrWhiteSpace(origin)) query = query.Where(x => x.l.Origin == origin);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = "%" + q.Trim().ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(x => EF.Functions.Like(x.c.NormalizedName, term) || EF.Functions.Like(x.l.BoardToken, term));
        }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.c.NormalizedName).ThenBy(x => x.l.Platform)
            .Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var watchers = await system.WatcherCountsAsync(rows.Select(r => r.l.Id).ToList(), ct);
        var items = rows.Select(r => ToDto(r.l, r.c, watchers.GetValueOrDefault(r.l.Id))).ToList();
        return TypedResults.Ok(new AdminCompanyPage(items, p, size, total));
    }

    private static AdminCompanyDto ToDto(CompanyAtsLink l, Company c, int watchers) => new(l.Id, c.Id, c.Name, c.WebsiteDomain,
        l.Platform, l.BoardToken, l.Status, l.Origin, l.Confidence, l.FailureCount, l.OpenJobsCount, l.LastCheckedAt, l.NextRetryAt,
        watchers, l.CreatedAt);

    private static async Task<Results<Created<AdminCompanyDto>, Ok<AdminCompanyDto>>> AddCompany(AddCompanyRequest body, AppDbContext db,
        CompanyLinker linker, BoardVerifier verifier, SystemQueries system, IBackgroundJobClient jobs, TimeProvider clock, CancellationToken ct)
    {
        var errors = new FieldErrors();
        var name = errors.Text("name", body.Name, 200, label: "Name");
        var token = body.BoardToken?.Trim().ToLowerInvariant();
        if (!AtsUrl.IsPlatform(body.Platform)) errors.Add("platform", "Choose greenhouse, lever or ashby.");
        if (!AtsUrl.IsValidToken(token)) errors.Add("boardToken", "Use 2-60 characters: a-z, 0-9, _ or -.");
        var domain = string.IsNullOrWhiteSpace(body.WebsiteDomain) ? null : CompanyLinker.Domain(body.WebsiteDomain);
        if (body.WebsiteDomain is { Length: > 0 } && domain is null) errors.Add("websiteDomain", "Use a domain such as acme.com.");
        errors.ThrowIfAny();

        BoardCheck check;
        try
        {
            check = await verifier.CheckAsync(body.Platform!, token!, ct);
        }
        catch (SourceException)
        {
            throw Unreachable();
        }
        if (!check.Exists) throw new ApiException(StatusCodes.Status422UnprocessableEntity, ErrorCodes.BoardNotFound, "Board not found",
            "That board does not exist on the platform.");

        var (link, created) = await linker.EnsureLinkAsync(body.Platform!, token!, name!, domain, LinkOrigin.Admin, ct);
        link.Status = LinkStatus.Resolved;
        link.OpenJobsCount = check.OpenJobs;
        link.LastCheckedAt = clock.GetUtcNow();
        link.NextRetryAt = null;
        link.FailureCount = 0;
        await db.SaveChangesAsync(ct);
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == link.CompanyId, ct);
        var dto = ToDto(link, company, (await system.WatcherCountsAsync([link.Id], ct)).GetValueOrDefault(link.Id));
        if (!created) return TypedResults.Ok(dto);
        var linkId = link.Id;
        jobs.Enqueue<PollBoardJob>(j => j.RunAsync(linkId, CancellationToken.None));
        return TypedResults.Created($"/api/v1/admin/companies/{link.Id}", dto);
    }

    private static async Task<Ok<AdminCompanyDto>> UpdateCompany(Guid linkId, UpdateCompanyRequest body, AppDbContext db,
        SystemQueries system, CancellationToken ct)
    {
        var link = await db.CompanyAtsLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct) ?? throw NotFound("board");
        if (body.Status is null || !LinkStatus.All.Contains(body.Status))
            throw FieldErrors.Single("status", "Choose resolved, not_found or blocked.");
        link.Status = body.Status;
        if (body.Status == LinkStatus.Resolved) link.NextRetryAt = null; // poll on the next cycle
        await db.SaveChangesAsync(ct);
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == link.CompanyId, ct);
        return TypedResults.Ok(ToDto(link, company, (await system.WatcherCountsAsync([link.Id], ct)).GetValueOrDefault(link.Id)));
    }

    private static async Task<Ok<AdminCompanyDto>> VerifyCompany(Guid linkId, AppDbContext db, BoardVerifier verifier,
        SystemQueries system, CancellationToken ct)
    {
        var link = await db.CompanyAtsLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct) ?? throw NotFound("board");
        try
        {
            await verifier.VerifyLinkAsync(link, ct);
        }
        catch (SourceException)
        {
            throw Unreachable();
        }
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == link.CompanyId, ct);
        return TypedResults.Ok(ToDto(link, company, (await system.WatcherCountsAsync([link.Id], ct)).GetValueOrDefault(link.Id)));
    }

    private static async Task<Accepted<SeedLoadResult>> LoadSeed(SeedLoader loader, CancellationToken ct)
    {
        var (added, skipped) = await loader.LoadAsync(ct);
        return TypedResults.Accepted((string?)null, new SeedLoadResult(added, skipped));
    }

    private static ApiException NotFound(string what) =>
        new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Not found", $"This {what} does not exist.");

    private static ApiException TermsNotReviewed(string detail) =>
        new(StatusCodes.Status409Conflict, ErrorCodes.TermsNotReviewed, "Terms not reviewed", detail);

    private static ApiException Unreachable() => new(StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServiceUnavailable,
        "Board unavailable", "The board could not be reached just now. Try again in a few minutes.");
}
