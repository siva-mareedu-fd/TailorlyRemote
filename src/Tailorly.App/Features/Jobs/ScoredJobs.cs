using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Features.Admin;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Jobs;
using Tailorly.Core.Matching;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Jobs;

/// <summary>One score of the caller with the job and enrichment fields that display and the digest rules need.</summary>
public sealed record ScoredRow
{
    public Guid ScoreId { get; init; }
    public Guid JobId { get; init; }
    public int ResumeVersion { get; init; }
    public short FitScore { get; init; }
    public required string Model { get; init; }
    public required string Reasons { get; init; }
    public required string MatchedSkills { get; init; }
    public required string MissingSkills { get; init; }
    public required string Confidence { get; init; }
    public DateTimeOffset ScoredAt { get; init; }

    public required string SourceKey { get; init; }
    public required string Title { get; init; }
    public required string CompanyName { get; init; }
    public string? LocationText { get; init; }
    public string? WorkMode { get; init; }
    public string? EmploymentType { get; init; }
    public long? SalaryMin { get; init; }
    public long? SalaryMax { get; init; }
    public string? SalaryCurrency { get; init; }
    public string? SalaryPeriodOriginal { get; init; }
    public required string SalaryKind { get; init; }
    public required string DescriptionCompleteness { get; init; }
    public required string ApplyUrl { get; init; }
    public required string SourceUrl { get; init; }
    public string? SourceMeta { get; init; }
    public required string DedupeKey { get; init; }
    public Guid? DuplicateOfJobId { get; init; }
    public DateTimeOffset? PostedAt { get; init; }
    public DateTimeOffset JobCreatedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? DescriptionHead { get; init; }

    /// <summary>Only loaded for the job detail.</summary>
    public string? Description { get; init; }

    public string? EnrichmentData { get; init; }
}

/// <summary>A scored job with the rules evaluated against the caller's current preferences.</summary>
/// <param name="Fit">The AI fit score, or null for a rule-ranked job.</param>
/// <param name="Open">Not suppressed as a duplicate, not closed on its board, not past the source's expiry.</param>
public sealed record ScoredView(
    ScoredRow Row,
    string Eligibility,
    string? EligibilityEvidence,
    Salary Salary,
    SalaryDecision SalaryDecision,
    ApproxInrDto? ApproxInr,
    int? Fit,
    string? WorkMode,
    string? EmploymentType,
    bool Open,
    EnrichmentOutput? Enrichment)
{
    /// <summary>
    /// Shown in the app (ApiDesign §7): open, not restricted, and the salary rule keeps it. Unclear eligibility is shown
    /// (with a warning badge in the SPA).
    /// </summary>
    public bool VisibleInApp => Open && EligibilityResolver.Visible(Eligibility, inEmail: false, showUnclearInEmail: false)
                                && SalaryDecision.Keep;

    /// <summary>The minimum match score applies to everything with an AI score (PRD §7.5 step 4).</summary>
    public bool MeetsMinScore(int min) => Fit is null || Fit >= min;

    public RankedItem Ranked => new(Row.JobId, Fit ?? Row.FitScore, Fit is null, Row.PostedAt, Row.JobCreatedAt);
}

/// <summary>
/// Reads the caller's scored jobs (never the shared pool, ApiDesign API10) and applies the display rules with the
/// caller's current preferences: eligibility display rule, salary rule (with FX conversion) and minimum score. Used by
/// <c>GET /jobs</c>, <c>GET /jobs/{id}</c>, digest assembly and the digest email, so all of them agree.
/// </summary>
public sealed class ScoredJobs(AppDbContext db, IFxRateProvider fx, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Scores (already scoped to the caller by the global filter) joined with their jobs and enrichment.</summary>
    public IQueryable<ScoredRow> Query(IQueryable<UserJobScore> scores, bool withDescription = false) =>
        from s in scores.AsNoTracking()
        join j in db.Jobs.AsNoTracking() on s.JobId equals j.Id
        join e in db.JobEnrichment.AsNoTracking() on j.Id equals e.JobId into ej
        from e in ej.DefaultIfEmpty()
        select new ScoredRow
        {
            ScoreId = s.Id, JobId = j.Id, ResumeVersion = s.ResumeVersion, FitScore = s.FitScore, Model = s.Model,
            Reasons = s.Reasons, MatchedSkills = s.MatchedSkills, MissingSkills = s.MissingSkills, Confidence = s.Confidence,
            ScoredAt = s.CreatedAt,
            SourceKey = j.SourceKey, Title = j.Title, CompanyName = j.CompanyName, LocationText = j.LocationText,
            WorkMode = j.WorkMode, EmploymentType = j.EmploymentType, SalaryMin = j.SalaryMin, SalaryMax = j.SalaryMax,
            SalaryCurrency = j.SalaryCurrency, SalaryPeriodOriginal = j.SalaryPeriodOriginal, SalaryKind = j.SalaryKind,
            DescriptionCompleteness = j.DescriptionCompleteness, ApplyUrl = j.ApplyUrl, SourceUrl = j.SourceUrl,
            SourceMeta = j.SourceMeta, DedupeKey = j.DedupeKey, DuplicateOfJobId = j.DuplicateOfJobId, PostedAt = j.PostedAt,
            JobCreatedAt = j.CreatedAt, ClosedAt = j.ClosedAt, ExpiresAt = j.ExpiresAt,
            DescriptionHead = j.DescriptionText == null ? null : j.DescriptionText.Substring(0, 500),
            Description = withDescription ? j.DescriptionText : null,
            EnrichmentData = e == null ? null : e.Data,
        };

    /// <summary>Evaluates the rules for each row with the given preferences (FX rates fetched once per currency).</summary>
    public async Task<List<ScoredView>> EvaluateAsync(IEnumerable<ScoredRow> rows, MatchPreferences prefs, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rates = new Dictionary<(string, string), decimal?>();
        async Task<decimal?> Rate(string from, string to)
        {
            if (!rates.TryGetValue((from, to), out var r)) rates[(from, to)] = r = await fx.GetRateAsync(from, to, ct);
            return r;
        }

        var views = new List<ScoredView>();
        foreach (var row in rows)
        {
            var enrichment = ParseEnrichment(row.EnrichmentData);
            var (eligibility, evidence) = enrichment is not null
                ? (enrichment.Enrichment.Eligibility, enrichment.Enrichment.EligibilityEvidence)
                : RulesEligibility(row);
            var salary = EffectiveSalary(row, enrichment?.Enrichment);
            var fit = row.Model == UserJobScore.RulesModel ? (int?)null : row.FitScore;
            var decision = SalaryRule.Apply(salary, prefs,
                salary.Currency is { } c ? await Rate(c, prefs.SalaryCurrency) : null, fit);
            ApproxInrDto? approx = null;
            if (salary.Currency is { } cur && cur != "INR" && (salary.Min is not null || salary.Max is not null)
                && await Rate(cur, "INR") is { } inr)
                approx = new ApproxInrDto(Convert(salary.Min, inr), Convert(salary.Max, inr));
            var open = row.DuplicateOfJobId is null && row.ClosedAt is null && (row.ExpiresAt is null || row.ExpiresAt > now);
            views.Add(new ScoredView(row, eligibility, evidence, salary, decision, approx, fit,
                enrichment?.Enrichment.WorkMode ?? row.WorkMode, enrichment?.Enrichment.EmploymentType ?? row.EmploymentType,
                open, enrichment?.Enrichment));
        }
        return views;
    }

    /// <summary>One view per job: the score for the newest résumé version (a new résumé re-scores jobs).</summary>
    public static List<ScoredView> LatestPerJob(IEnumerable<ScoredView> views) => views
        .GroupBy(v => v.Row.JobId)
        .Select(g => g.OrderByDescending(v => v.Row.ResumeVersion).ThenByDescending(v => v.Row.ScoredAt).First())
        .ToList();

    public static IReadOnlyList<ScoredView> Rank(IReadOnlyCollection<ScoredView> views, bool byPosted = false)
    {
        var byId = views.ToDictionary(v => v.Row.JobId);
        if (byPosted)
            return views.OrderByDescending(v => v.Row.PostedAt ?? v.Row.JobCreatedAt).ThenBy(v => v.Row.JobId).ToList();
        return Ranking.Order(views.Select(v => v.Ranked)).Select(r => byId[r.JobId]).ToList();
    }

    public async Task<List<JobSummaryDto>> ToSummariesAsync(IReadOnlyList<ScoredView> views, CancellationToken ct)
    {
        var (sources, alsoOn) = await LookupsAsync(views, ct);
        var tracked = await TrackedAsync(views.Select(v => v.Row.JobId).ToList(), ct);
        return views.Select(v => Summary(v, sources, alsoOn) with { Tracked = tracked.GetValueOrDefault(v.Row.JobId) }).ToList();
    }

    public async Task<JobDetailDto> ToDetailAsync(ScoredView v, CancellationToken ct)
    {
        var (sources, alsoOn) = await LookupsAsync([v], ct);
        var tracked = await TrackedAsync([v.Row.JobId], ct);
        var e = v.Enrichment;
        return new JobDetailDto
        {
            Id = v.Row.JobId, Title = v.Row.Title, CompanyName = v.Row.CompanyName, LocationText = v.Row.LocationText,
            WorkMode = v.WorkMode, EmploymentType = v.EmploymentType, Eligibility = v.Eligibility,
            EligibilityEvidence = v.EligibilityEvidence, Salary = SalaryDto(v), SalaryLabel = v.SalaryDecision.Label,
            DescriptionCompleteness = v.Row.DescriptionCompleteness, PostedAt = v.Row.PostedAt, ApplyUrl = ApplyUrl(v.Row),
            Source = Source(v.Row, sources), AlsoOn = alsoOn.GetValueOrDefault(v.Row.JobId, []), Score = Score(v),
            Tracked = tracked.GetValueOrDefault(v.Row.JobId),
            Description = v.Row.Description, Summary = e?.Summary, SkillsRequired = e?.SkillsRequired ?? [],
            SkillsPreferred = e?.SkillsPreferred ?? [], Seniority = e?.Seniority, TimezoneRequirement = e?.TimezoneRequirement,
        };
    }

    /// <summary>The caller's tracker entries for these jobs (through the user filter).</summary>
    public async Task<Dictionary<Guid, TrackedDto>> TrackedAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken ct) =>
        jobIds.Count == 0
            ? []
            : await db.UserJobs.AsNoTracking().Where(u => u.JobId != null && jobIds.Contains(u.JobId.Value))
                .ToDictionaryAsync(u => u.JobId!.Value, u => new TrackedDto(u.Id, u.Status), ct);

    private static JobSummaryDto Summary(ScoredView v, IReadOnlyDictionary<string, JobSource> sources,
        IReadOnlyDictionary<Guid, List<AlsoOnDto>> alsoOn) => new()
    {
        Id = v.Row.JobId, Title = v.Row.Title, CompanyName = v.Row.CompanyName, LocationText = v.Row.LocationText,
        WorkMode = v.WorkMode, EmploymentType = v.EmploymentType, Eligibility = v.Eligibility,
        EligibilityEvidence = v.EligibilityEvidence, Salary = SalaryDto(v), SalaryLabel = v.SalaryDecision.Label,
        DescriptionCompleteness = v.Row.DescriptionCompleteness, PostedAt = v.Row.PostedAt, ApplyUrl = ApplyUrl(v.Row),
        Source = Source(v.Row, sources), AlsoOn = alsoOn.GetValueOrDefault(v.Row.JobId, []), Score = Score(v),
    };

    private async Task<(Dictionary<string, JobSource>, Dictionary<Guid, List<AlsoOnDto>>)> LookupsAsync(
        IReadOnlyCollection<ScoredView> views, CancellationToken ct)
    {
        var sources = await db.JobSources.AsNoTracking().ToDictionaryAsync(s => s.Key, StringComparer.Ordinal, ct);
        var keys = views.Select(v => v.Row.DedupeKey).Distinct().ToList();
        var others = keys.Count == 0
            ? []
            : await db.Jobs.AsNoTracking()
                .Where(j => keys.Contains(j.DedupeKey) && j.ClosedAt == null)
                .Select(j => new { j.Id, j.DedupeKey, j.SourceKey, j.SourceUrl })
                .ToListAsync(ct);
        var byKey = others.ToLookup(o => o.DedupeKey);
        var alsoOn = views.ToDictionary(v => v.Row.JobId, v => byKey[v.Row.DedupeKey]
            .Where(o => o.Id != v.Row.JobId && o.SourceKey != v.Row.SourceKey)
            .Select(o => new AlsoOnDto(o.SourceKey, sources.GetValueOrDefault(o.SourceKey)?.DisplayName ?? o.SourceKey, o.SourceUrl))
            .DistinctBy(a => a.SourceKey)
            .ToList());
        return (sources, alsoOn);
    }

    private static SourceInfoDto Source(ScoredRow r, IReadOnlyDictionary<string, JobSource> sources)
    {
        var s = sources.GetValueOrDefault(r.SourceKey);
        return new SourceInfoDto(r.SourceKey, s?.DisplayName ?? r.SourceKey, s?.AttributionText, s?.AttributionUrl, r.SourceUrl);
    }

    /// <summary>The source's apply link; a non-http(s) value (never expected) falls back to the listing page.</summary>
    internal static string ApplyUrl(ScoredRow r) => IsHttp(r.ApplyUrl) ? r.ApplyUrl : r.SourceUrl;

    internal static bool IsHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    private static SalaryDto SalaryDto(ScoredView v) => new(v.Salary.Min, v.Salary.Max, v.Salary.Currency, "year",
        v.Salary.PeriodOriginal, v.Salary.Kind, v.ApproxInr);

    private static JobScoreDto? Score(ScoredView v) => v.Fit is not { } fit
        ? null
        : new JobScoreDto(fit, Strings(v.Row.Reasons), Strings(v.Row.MatchedSkills), Strings(v.Row.MissingSkills), v.Row.Confidence);

    internal static IReadOnlyList<string> Strings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static EnrichmentData? ParseEnrichment(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<EnrichmentData>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Eligibility of a job without enrichment (rule-ranked): structured source fields and phrase rules.</summary>
    private static (string, string?) RulesEligibility(ScoredRow r)
    {
        var hint = EligibilityRules.Classify(CoverageReport.Countries(r.SourceMeta), r.LocationText, r.Title, r.DescriptionHead);
        return (hint.Eligibility, hint.Evidence);
    }

    /// <summary>The listing's own salary; if it has none, a salary the enrichment found stated in the text.</summary>
    internal static Salary EffectiveSalary(ScoredRow r, EnrichmentOutput? e)
    {
        var own = new Salary(r.SalaryMin, r.SalaryMax, r.SalaryCurrency?.Trim(), r.SalaryPeriodOriginal, r.SalaryKind);
        if (SalaryLabels.For(own) != SalaryLabels.NotListed || e?.SalaryExtracted is not { } x) return own;
        return SalaryParser.FromStructured(x.Min, x.Max, x.Currency, x.Period, estimate: false);
    }

    private static long? Convert(long? amount, decimal rate) =>
        amount is { } a ? (long)decimal.Round(a * rate, 0, MidpointRounding.AwayFromZero) : null;
}
