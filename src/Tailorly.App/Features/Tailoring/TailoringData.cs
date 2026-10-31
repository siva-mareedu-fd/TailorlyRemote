using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Ai;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Api;
using Tailorly.App.Features.Accounts;
using Tailorly.App.Features.Digests;
using Tailorly.App.Features.Resumes;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Jobs;
using Tailorly.Core.Matching;
using Tailorly.Core.Ports;
using Tailorly.Docx;
using Tailorly.Docx.Tailoring;

namespace Tailorly.App.Features.Tailoring;

/// <summary>The job's text is not available (listing limited or gone, nothing pasted): 422 <c>JOB_DESCRIPTION_REQUIRED</c>.</summary>
public sealed class JobDescriptionMissingException() : Exception("The job description is not available.");

/// <summary>
/// Docx work runs with bounded concurrency (2) and a per-operation timeout (SDD §10.5, §16.2), shared by parsing,
/// analysis, tailoring and finalising.
/// </summary>
public static class DocxWork
{
    private static readonly SemaphoreSlim Gate = new(2, 2);

    public static async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await Task.Run(work, ct).WaitAsync(DocxLimits.ParseTimeout, ct);
        }
        finally
        {
            Gate.Release();
        }
    }
}

/// <summary>
/// Loading and checks shared by the tailoring endpoints and jobs. Everything reads through the user-scoped context, so
/// another user's ids resolve to nothing (404).
/// </summary>
public sealed class TailoringData(
    AppDbContext db,
    IFileStore files,
    AccountState account,
    IConfiguration config,
    BudgetService budgets,
    EnrichmentService enrichment,
    IAiTaskRunner ai,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ApiException NotFound(string what = "This tracked job does not exist.") =>
        new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Not found", what);

    public static ApiException ResumeRequired() => new(StatusCodes.Status409Conflict, ErrorCodes.ResumeRequired,
        "Résumé required", "Upload a résumé and wait until it is ready, or make a ready version active.");

    public static ApiException DescriptionRequired() => new(StatusCodes.Status422UnprocessableEntity, ErrorCodes.JobDescriptionRequired,
        "Job description required", "We do not have the full description of this job. Paste it to continue.",
        new Dictionary<string, object?> { ["action"] = "paste_description" });

    // ---------- preconditions (ApiDesign §4.8, in this order after the 404) ----------

    public async Task<UserJob> UserJobAsync(Guid id, CancellationToken ct) =>
        await db.UserJobs.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw NotFound();

    public async Task RequireConsentAsync(CancellationToken ct)
    {
        var state = await account.GetAsync(ct);
        if (state?.ConsentVersion != AuthEndpoints.CurrentConsentVersion(config)) throw AuthEndpoints.ConsentRequired();
    }

    /// <summary>The given résumé if it is ready, else the active ready one; 409 <c>RESUME_REQUIRED</c> otherwise.</summary>
    public async Task<Resume> ResumeAsync(Guid? resumeId, CancellationToken ct)
    {
        var resume = resumeId is { } id
            ? await db.Resumes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
            : await db.Resumes.AsNoTracking().FirstOrDefaultAsync(r => r.IsActive, ct);
        if (resumeId is not null && resume is null) throw NotFound("This résumé does not exist.");
        if (resume is null || resume.ParseStatus != WorkStatus.Ready) throw ResumeRequired();
        return resume;
    }

    public async Task RequireDescriptionAsync(UserJob uj, CancellationToken ct)
    {
        if (uj.UserDescription is not null) return;
        var full = uj.JobId is { } jobId && await db.Jobs.AsNoTracking()
            .AnyAsync(j => j.Id == jobId && j.DescriptionText != null && j.DescriptionCompleteness == Completeness.Full, ct);
        if (!full) throw DescriptionRequired();
    }

    public async Task RequireBudgetAsync(Guid userId, CancellationToken ct) => await ResumeEndpoints.ThrowIfBudgetExhausted(budgets, userId, ct);

    // ---------- what jobs need ----------

    public async Task<ParsedProfile?> ProfileAsync(Guid resumeId, CancellationToken ct)
    {
        var row = await db.ResumeProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.ResumeId == resumeId, ct);
        return row is null ? null : ResumeEndpoints.ToDto(row).Effective;
    }

    public Task<List<string>> ConfirmedSkillsAsync(CancellationToken ct) =>
        db.UserConfirmedSkills.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.Skill).ToListAsync(ct);

    public async Task<byte[]> FileAsync(string storageKey, CancellationToken ct)
    {
        await using var stream = await files.OpenReadAsync(storageKey, ct) ?? throw new FileNotFoundException("A stored file is missing.");
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public static Task<DocModel> ExtractAsync(byte[] docx, CancellationToken ct) =>
        DocxWork.RunAsync(() => ResumeExtractor.Extract(new MemoryStream(docx, writable: false)), ct);

    /// <summary>
    /// The job's text and enrichment. A pasted description wins (the user pasted it because the listing's text was limited
    /// or gone); it is enriched once with <c>byo_job_enrichment</c>, charged to the user, and stored on the tracker entry.
    /// Otherwise the listing's full text and its shared enrichment, created on demand as a platform cost (SDD §8.5).
    /// </summary>
    /// <exception cref="JobDescriptionMissingException">No usable text.</exception>
    public async Task<TailoringJob> JobAsync(UserJob uj, Guid userId, CancellationToken ct)
    {
        if (uj.UserDescription is { } pasted)
        {
            var stored = ScoredJobsParse(uj.UserEnrichment);
            if (stored is null)
            {
                var hint = EligibilityRules.Classify(null, null, uj.Title, pasted);
                var input = JobEnrichmentTask.BuildInput(new EnrichmentJobInput(uj.Title, uj.CompanyName, null, null, null, null, pasted), hint);
                var result = await ai.RunAsync(JobEnrichmentTask.ByoDefinition, input, userId, ct);
                var evidence = JobEnrichmentTask.QuotedEvidence(result.Value.EligibilityEvidence, $"{uj.Title}\n{pasted}");
                var resolved = EligibilityResolver.Resolve(hint, result.Value.Eligibility, evidence);
                stored = new EnrichmentData(JobEnrichmentTask.SchemaVersion, resolved.Source, result.Value.Eligibility,
                    result.Value with { Eligibility = resolved.Eligibility, EligibilityEvidence = resolved.Evidence });
                await db.UserJobs.Where(u => u.Id == uj.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.UserEnrichment, JsonSerializer.Serialize(stored, Json))
                    .SetProperty(u => u.UpdatedAt, clock.GetUtcNow()), ct);
            }
            return new TailoringJob(uj.Title, uj.CompanyName, pasted, stored.Enrichment);
        }

        var job = uj.JobId is { } jobId
            ? await db.Jobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => new { j.Id, j.DescriptionText, j.DescriptionCompleteness })
                .FirstOrDefaultAsync(ct)
            : null;
        if (job?.DescriptionText is null || job.DescriptionCompleteness != Completeness.Full) throw new JobDescriptionMissingException();

        var data = await EnrichmentDataAsync(job.Id, ct);
        if (data is null)
        {
            await enrichment.EnsureAsync([job.Id], ct);
            data = await EnrichmentDataAsync(job.Id, ct);
        }
        return new TailoringJob(uj.Title, uj.CompanyName, job.DescriptionText, data?.Enrichment);
    }

    private async Task<EnrichmentData?> EnrichmentDataAsync(Guid jobId, CancellationToken ct) =>
        ScoredJobsParse(await db.JobEnrichment.AsNoTracking().Where(e => e.JobId == jobId).Select(e => e.Data).FirstOrDefaultAsync(ct));

    private static EnrichmentData? ScoredJobsParse(string? json) => Tailorly.App.Features.Jobs.ScoredJobs.ParseEnrichment(json);

    /// <summary>What the user has stated: résumé text, profile, confirmed skills, plus anything else allowed (job text for letters).</summary>
    public static FactSource Facts(DocModel resume, ParsedProfile? profile, IEnumerable<string> confirmed, params string?[] more) =>
        FactSource.Build(resume.Paragraphs.Select(p => p.Text)
            .Concat(resume.HeaderFooterLines)
            .Concat(profile is null ? [] : ProfileFacts.Strings(profile))
            .Concat(confirmed)
            .Concat(more.OfType<string>()));

    /// <summary>The job's skills with their aliases (for the new-term check), plus the analysis gaps.</summary>
    public static IReadOnlyList<SkillGroup> SkillGroups(EnrichmentOutput? e, IEnumerable<string>? extra = null)
    {
        var groups = new List<SkillGroup>();
        if (e is not null)
        {
            var aliases = e.SkillAliases.GroupBy(a => a.Skill, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.SelectMany(a => a.Aliases).ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var s in e.SkillsRequired.Concat(e.SkillsPreferred).Where(s => !string.IsNullOrWhiteSpace(s)))
                groups.Add(new SkillGroup(s, aliases.GetValueOrDefault(s) ?? []));
        }
        foreach (var s in extra ?? [])
            if (!string.IsNullOrWhiteSpace(s) && !groups.Any(g => string.Equals(g.Skill, s, StringComparison.OrdinalIgnoreCase)))
                groups.Add(new SkillGroup(s, []));
        return groups;
    }
}
