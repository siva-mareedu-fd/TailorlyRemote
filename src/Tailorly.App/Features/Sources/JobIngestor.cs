using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Sources;

/// <summary>Counts for one ingested batch (summed into <c>source_health.last_run_stats</c>).</summary>
public sealed record IngestResult(int Received, int New, int Updated, int Unchanged, int Invalid, IReadOnlyList<Guid> DiscoveredLinks)
{
    /// <summary>Every listing on the page was already stored (the "all-known page" stop rule).</summary>
    public bool AllKnown => Received > 0 && New == 0;
}

/// <summary>
/// The shared write path for every source (SDD §7.2, §8.2–8.4): normalise each <see cref="RawJob"/>, upsert it by
/// <c>(source_key, external_id)</c>, link it to a known company, re-pick the canonical listing for every touched dedupe
/// key, and (for feed sources that allow it) discover ATS boards from apply links. Never logs job text.
/// </summary>
public sealed class JobIngestor(AppDbContext db, DedupeResolver dedupe, CompanyLinker companies, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="companyId">The board's company (Path B); null for feeds, which are looked up by name.</param>
    public async Task<IngestResult> IngestAsync(JobSource source, IReadOnlyList<RawJob> raws, Guid? companyId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var traits = new SourceTraits(RemoteOnly: source.Kind == SourceKinds.Feed);
        var postings = new List<(RawJob Raw, JobPosting Posting)>();
        var invalid = 0;
        foreach (var raw in raws.DistinctBy(r => r.ExternalId))
        {
            if (raw.SourceKey != source.Key || !JobNormalizer.IsHttpUrl(raw.ApplyUrl) || !JobNormalizer.IsHttpUrl(raw.SourceUrl))
            {
                invalid++;
                continue;
            }
            var p = JobNormalizer.Normalize(raw, traits);
            if (p.Title.Length == 0 || p.CompanyName.Length == 0) invalid++;
            else postings.Add((raw, p));
        }

        var ids = postings.Select(x => x.Posting.ExternalId).ToList();
        var existing = await db.Jobs.Where(j => j.SourceKey == source.Key && ids.Contains(j.ExternalId))
            .ToDictionaryAsync(j => j.ExternalId, StringComparer.Ordinal, ct);

        var touchedKeys = new HashSet<string>(StringComparer.Ordinal);
        int created = 0, updated = 0, unchanged = 0;
        foreach (var (raw, p) in postings)
        {
            var company = companyId ?? await companies.LookupCompanyIdAsync(p.CompanyName, p.CompanyWebsite, ct);
            if (!existing.TryGetValue(p.ExternalId, out var job))
            {
                job = new Job { SourceKey = source.Key, ExternalId = p.ExternalId, DedupeKey = p.DedupeKey, CompanyName = p.CompanyName,
                    Title = p.Title, ApplyUrl = p.ApplyUrl, SourceUrl = p.SourceUrl, CreatedAt = now };
                Apply(job, raw, p, company, now);
                db.Jobs.Add(job);
                touchedKeys.Add(p.DedupeKey);
                created++;
                continue;
            }

            var changed = job.ContentHash != p.ContentHash || job.DedupeKey != p.DedupeKey || job.Title != p.Title || job.CompanyName != p.CompanyName
                          || job.LocationText != p.LocationText || job.ExpiresAt != p.ExpiresAt || job.ClosedAt is not null
                          || job.ApplyUrl != p.ApplyUrl || (company is not null && job.CompanyId != company);
            job.FetchedAt = now;
            if (!changed)
            {
                unchanged++;
                continue;
            }
            if (job.DedupeKey != p.DedupeKey) touchedKeys.Add(job.DedupeKey);
            Apply(job, raw, p, company ?? job.CompanyId, now);
            touchedKeys.Add(p.DedupeKey);
            updated++;
        }
        await db.SaveChangesAsync(ct);
        await dedupe.ResolveAsync(touchedKeys, ct);

        var discovered = source.Kind == SourceKinds.Feed && !source.ForbidThirdPartyResolution
            ? await companies.DiscoverFromJobsAsync(postings.Select(x => (x.Raw, x.Posting.CompanyName)).ToList(), ct)
            : [];
        return new IngestResult(raws.Count, created, updated, unchanged, invalid, discovered);
    }

    private static void Apply(Job job, RawJob raw, JobPosting p, Guid? companyId, DateTimeOffset now)
    {
        job.DedupeKey = p.DedupeKey;
        job.CompanyId = companyId;
        job.CompanyName = p.CompanyName;
        job.Title = p.Title;
        job.LocationText = p.LocationText;
        job.LocationClass = p.LocationClass;
        job.WorkMode = p.WorkMode;
        job.EmploymentType = p.EmploymentType;
        job.HiringModelHint = p.HiringModelHint;
        job.SalaryMin = p.Salary.Min;
        job.SalaryMax = p.Salary.Max;
        job.SalaryCurrency = p.Salary.Currency;
        job.SalaryPeriodOriginal = p.Salary.PeriodOriginal;
        job.SalaryKind = p.Salary.Kind;
        job.DescriptionText = p.DescriptionText.Length == 0 ? null : p.DescriptionText;
        job.DescriptionCompleteness = p.DescriptionFull ? Completeness.Full : Completeness.Limited;
        job.ApplyUrl = p.ApplyUrl; // always the source's own link (PRD §7.1a); never replaced by an ATS link
        job.SourceUrl = p.SourceUrl;
        job.SourceMeta = JsonSerializer.Serialize(raw.SourceMeta, Json);
        job.ContentHash = p.ContentHash;
        job.PostedAt = p.PostedAt;
        job.ExpiresAt = p.ExpiresAt;
        job.ClosedAt = null;
        job.FetchedAt = now;
    }
}

/// <summary>
/// Re-picks the canonical listing for dedupe keys (DatabaseDesign §7 "Dedupe on upsert", rules in
/// <see cref="DedupeRanking"/>). Keys are locked with transaction-scoped advisory locks in a fixed order, so two sources
/// ingesting the same job at once cannot interleave. Closed listings take no part and are never marked as duplicates.
/// </summary>
public sealed class DedupeResolver(AppDbContext db)
{
    public async Task<int> ResolveAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0) return 0;
        var changed = 0;
        foreach (var chunk in keys.Order(StringComparer.Ordinal).Chunk(200))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var key in chunk)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);

            var rows = await (from j in db.Jobs
                              join s in db.JobSources on j.SourceKey equals s.Key
                              where chunk.Contains(j.DedupeKey)
                              select new { Job = j, s.Kind, s.RequireSourceApplyUrl }).ToListAsync(ct);
            foreach (var group in rows.GroupBy(r => r.Job.DedupeKey))
            {
                var open = group.Where(r => r.Job.ClosedAt is null).ToList();
                Guid? winner = open.Count == 0 ? null : DedupeRanking.Winner(open.Select(r => new DedupeCandidate(r.Job.Id,
                    r.Kind == SourceKinds.Board, r.RequireSourceApplyUrl,
                    r.Job.DescriptionCompleteness == Completeness.Full, r.Job.CreatedAt)).ToList()).Id;
                foreach (var r in group)
                {
                    Guid? target = r.Job.ClosedAt is null && r.Job.Id != winner ? winner : null;
                    if (r.Job.DuplicateOfJobId == target) continue;
                    r.Job.DuplicateOfJobId = target;
                    changed++;
                }
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return changed;
    }
}
