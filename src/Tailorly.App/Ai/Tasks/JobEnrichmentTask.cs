using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Tailorly.Core.Jobs;

namespace Tailorly.App.Ai.Tasks;

// Shape of the job_enrichment reply (PRD Appendix B; keys snake_case as there). skill_aliases is a list of
// {skill, aliases} because structured outputs cannot express a free-form map.

public sealed record SkillAlias(
    [property: JsonPropertyName("skill")] string Skill,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases);

public sealed record ExtractedSalary(
    [property: JsonPropertyName("min")] decimal? Min,
    [property: JsonPropertyName("max")] decimal? Max,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("period")] string? Period);

public sealed record EnrichmentOutput(
    [property: JsonPropertyName("skills_required")] IReadOnlyList<string> SkillsRequired,
    [property: JsonPropertyName("skills_preferred")] IReadOnlyList<string> SkillsPreferred,
    [property: JsonPropertyName("skill_aliases")] IReadOnlyList<SkillAlias> SkillAliases,
    [property: JsonPropertyName("seniority")] string? Seniority,
    [property: JsonPropertyName("years_experience_min")] int? YearsExperienceMin,
    [property: JsonPropertyName("years_experience_max")] int? YearsExperienceMax,
    [property: JsonPropertyName("employment_type")] string? EmploymentType,
    [property: JsonPropertyName("work_mode")] string? WorkMode,
    [property: JsonPropertyName("eligibility")] string Eligibility,
    [property: JsonPropertyName("eligibility_evidence")] string? EligibilityEvidence,
    [property: JsonPropertyName("restricted_regions")] IReadOnlyList<string> RestrictedRegions,
    [property: JsonPropertyName("timezone_requirement")] string? TimezoneRequirement,
    [property: JsonPropertyName("hiring_model_hint")] string HiringModelHint,
    [property: JsonPropertyName("salary_extracted")] ExtractedSalary? SalaryExtracted,
    [property: JsonPropertyName("summary")] string Summary);

/// <summary>
/// What is stored in <c>job_enrichment.data</c>: the validated reply with the <b>final</b> eligibility and evidence
/// (SDD §8.6) and who decided it (<c>source</c>, <c>rules</c> or <c>model</c>), plus the schema version.
/// </summary>
public sealed record EnrichmentData(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("eligibility_source")] string EligibilitySource,
    [property: JsonPropertyName("model_eligibility")] string ModelEligibility,
    [property: JsonPropertyName("enrichment")] EnrichmentOutput Enrichment);

/// <summary>The job fields sent to <c>job_enrichment</c>. The description is untrusted text from a job board.</summary>
public sealed record EnrichmentJobInput(
    string Title,
    string CompanyName,
    string? LocationText,
    string? WorkMode,
    string? EmploymentType,
    IReadOnlyList<string>? Countries,
    string? Description);

/// <summary>
/// <c>job_enrichment</c>: structured facts and India eligibility for one job (PRD F5, §7.4). Shared by every user and a
/// platform cost (not charged to a user; protected by the daily platform ceiling).
/// </summary>
public static class JobEnrichmentTask
{
    public const int SchemaVersion = 1;
    public const int MaxDescriptionChars = 12_000;
    public const int MaxSummaryWords = 60;

    public static readonly AiTask<EnrichmentOutput> Definition = new(AiTaskNames.JobEnrichment, Validate);

    /// <summary>
    /// The same extraction on a description the user pasted (PRD F12, ApiDesign §4.7): charged to the user's Documents
    /// budget because the user started it, and stored on the tracker entry, never in the shared table.
    /// </summary>
    public static readonly AiTask<EnrichmentOutput> ByoDefinition = new(AiTaskNames.ByoJobEnrichment, Validate, AiTaskNames.JobEnrichment);

    /// <summary>The posting in <c>&lt;job_data&gt;</c> and the deterministic hints in <c>&lt;rule_hints&gt;</c>.</summary>
    public static AiTaskInput BuildInput(EnrichmentJobInput job, EligibilityHint hint)
    {
        var data = new StringBuilder()
            .Append("Title: ").AppendLine(job.Title)
            .Append("Company: ").AppendLine(job.CompanyName)
            .Append("Location: ").AppendLine(job.LocationText ?? "not stated")
            .Append("Work mode (from the source): ").AppendLine(job.WorkMode ?? "not stated")
            .Append("Employment type (from the source): ").AppendLine(job.EmploymentType ?? "not stated")
            .AppendLine("Description:")
            .Append(Truncate(job.Description, MaxDescriptionChars) ?? "(no description)");

        var hints = new StringBuilder()
            .Append("Structured countries from the source: ")
            .AppendLine(job.Countries is null ? "none" : job.Countries.Count == 0 ? "open worldwide" : string.Join(", ", job.Countries))
            .Append("Rule result: ").Append(hint.Eligibility)
            .Append(hint.Evidence is null ? "" : $" (evidence: \"{hint.Evidence}\")");

        return new AiTaskInput([new UntrustedBlock("job_data", data.ToString()), new UntrustedBlock("rule_hints", hints.ToString())]);
    }

    /// <summary>Deterministic checks the schema cannot express. Messages carry no job text.</summary>
    internal static IReadOnlyList<string> Validate(EnrichmentOutput e)
    {
        var errors = new List<string>();
        if (WordCount(e.Summary) > MaxSummaryWords) errors.Add($"summary has more than {MaxSummaryWords} words.");
        if (e.YearsExperienceMin is { } lo && e.YearsExperienceMax is { } hi && lo > hi)
            errors.Add("years_experience_min is greater than years_experience_max.");
        if (e.SalaryExtracted is { Min: { } smin, Max: { } smax } && smin > smax)
            errors.Add("salary_extracted.min is greater than salary_extracted.max.");
        if (e.SkillsRequired.Any(string.IsNullOrWhiteSpace) || e.SkillsPreferred.Any(string.IsNullOrWhiteSpace))
            errors.Add("skills lists contain a blank entry.");
        return errors;
    }

    /// <summary>
    /// The evidence is shown to the user as a quote, so it must come from the posting: kept only if it appears in the
    /// description (whitespace and case folded), otherwise dropped rather than shown as something the job never said.
    /// </summary>
    public static string? QuotedEvidence(string? evidence, string? description)
    {
        if (string.IsNullOrWhiteSpace(evidence) || string.IsNullOrWhiteSpace(description)) return null;
        var e = Fold(evidence.Trim().Trim('"', '“', '”', '\''));
        if (e.Length == 0) return null;
        return Fold(description).Contains(e, StringComparison.Ordinal) ? evidence.Trim().Trim('"', '“', '”') : null;

        static string Fold(string s) =>
            string.Join(' ', s.ToLower(CultureInfo.InvariantCulture).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    internal static int WordCount(string? s) =>
        string.IsNullOrWhiteSpace(s) ? 0 : s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
