using Microsoft.EntityFrameworkCore;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Sources;

/// <summary>
/// Company → ATS resolution, stage 1 (PRD §7.3 steps 1, 2, 4; SDD §8.4): parse apply URLs, look companies up by website
/// domain then normalised name, and persist links. No slug guessing (M7) and no URL is ever fetched here: discovered tokens
/// are only polled through the adapters' fixed URL templates. Callers must not pass jobs from sources with
/// <c>forbid_third_party_resolution</c> (Himalayas; Adzuna if ever built).
/// </summary>
public sealed class CompanyLinker(AppDbContext db, TimeProvider clock)
{
    private readonly Dictionary<string, Guid?> _lookups = new(StringComparer.Ordinal);

    /// <summary>The company id for a listing's company, if that company is known (by domain, then normalised name).</summary>
    public async Task<Guid?> LookupCompanyIdAsync(string companyName, string? website, CancellationToken ct)
    {
        var domain = Domain(website);
        var normalized = NameNormalizer.Company(companyName);
        var cacheKey = domain + "|" + normalized;
        if (_lookups.TryGetValue(cacheKey, out var cached)) return cached;

        Guid? id = null;
        if (domain is not null)
            id = await db.Companies.AsNoTracking().Where(c => c.WebsiteDomain == domain).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (id is null && normalized.Length > 0)
            id = await db.Companies.AsNoTracking().Where(c => c.NormalizedName == normalized).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        _lookups[cacheKey] = id;
        return id;
    }

    /// <summary>
    /// Feeder 3 (PRD §7.1b): ATS boards named in feed listings' apply links (and links inside the description HTML) are
    /// added as <c>discovered</c>. Returns the ids of links that did not exist before, for an eager first poll.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> DiscoverFromJobsAsync(IReadOnlyList<(RawJob Raw, string CompanyName)> jobs, CancellationToken ct)
    {
        var added = new List<Guid>();
        var seen = new HashSet<(string, string)>();
        foreach (var (raw, company) in jobs)
        {
            var refs = new List<AtsBoardRef>();
            if (AtsUrl.TryParse(raw.ApplyUrl, out var fromApply)) refs.Add(fromApply);
            refs.AddRange(AtsUrl.FindAll(raw.DescriptionHtml));
            foreach (var board in refs.Where(b => seen.Add((b.Platform, b.BoardToken))))
            {
                var (link, created) = await EnsureLinkAsync(board.Platform, board.BoardToken, company, Domain(raw.CompanyWebsite),
                    LinkOrigin.Discovered, ct);
                if (created) added.Add(link.Id);
            }
        }
        return added;
    }

    /// <summary>
    /// Finds or creates the company (by normalised name) and its link (by platform + token). An existing link keeps its
    /// origin and status; <paramref name="created"/> says whether the link is new.
    /// </summary>
    public async Task<(CompanyAtsLink Link, bool Created)> EnsureLinkAsync(string platform, string token, string companyName,
        string? websiteDomain, string origin, CancellationToken ct)
    {
        AtsUrl.RequireToken(token);
        if (!AtsUrl.IsPlatform(platform)) throw new ArgumentException("Unknown ATS platform.", nameof(platform));
        if (await db.CompanyAtsLinks.FirstOrDefaultAsync(l => l.Platform == platform && l.BoardToken == token, ct) is { } existing)
            return (existing, false);

        var name = string.IsNullOrWhiteSpace(companyName) ? token : companyName.Trim();
        if (name.Length > 200) name = name[..200];
        var companyId = await EnsureCompanyAsync(name, websiteDomain, ct);
        var now = clock.GetUtcNow();
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO company_ats_links (id, company_id, platform, board_token, status, origin, confidence, created_at)
            VALUES ({Guid.CreateVersion7()}, {companyId}, {platform}, {token}, 'resolved', {origin}, 'high', {now})
            ON CONFLICT (platform, board_token) DO NOTHING
            """, ct);
        var link = await db.CompanyAtsLinks.FirstAsync(l => l.Platform == platform && l.BoardToken == token, ct);
        return (link, inserted == 1);
    }

    private async Task<Guid> EnsureCompanyAsync(string name, string? websiteDomain, CancellationToken ct)
    {
        var normalized = NameNormalizer.Company(name);
        if (normalized.Length == 0) normalized = name.ToLowerInvariant();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO companies (id, name, normalized_name, website_domain, created_at)
            VALUES ({Guid.CreateVersion7()}, {name}, {normalized}, {Domain(websiteDomain)}, {clock.GetUtcNow()})
            ON CONFLICT (normalized_name) DO NOTHING
            """, ct);
        _lookups.Clear();
        return await db.Companies.Where(c => c.NormalizedName == normalized).Select(c => c.Id).FirstAsync(ct);
    }

    /// <summary><c>https://www.acme.com/careers</c> or <c>acme.com</c> → <c>acme.com</c>; null when not a plain host name.</summary>
    public static string? Domain(string? website)
    {
        if (string.IsNullOrWhiteSpace(website)) return null;
        var w = website.Trim();
        if (!w.Contains("://", StringComparison.Ordinal)) w = "https://" + w;
        if (!Uri.TryCreate(w, UriKind.Absolute, out var u) || u.HostNameType != UriHostNameType.Dns) return null;
        var host = u.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return host.Contains('.', StringComparison.Ordinal) && host.Length <= 253 ? host : null;
    }
}
