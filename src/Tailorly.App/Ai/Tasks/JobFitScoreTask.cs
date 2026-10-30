using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Tailorly.App.Ai.Tasks;

/// <summary>Shape of the job_fit_score reply (PRD Appendix B).</summary>
public sealed record FitScoreOutput(
    [property: JsonPropertyName("fit_score")] int FitScore,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons,
    [property: JsonPropertyName("matched_skills")] IReadOnlyList<string> MatchedSkills,
    [property: JsonPropertyName("missing_skills")] IReadOnlyList<string> MissingSkills,
    [property: JsonPropertyName("confidence")] string Confidence);

/// <summary>The preferences that shape a fit judgement (not the filters, which run before scoring).</summary>
public sealed record FitPreferences(IReadOnlyList<string> TargetRoles, string? ExperienceLevel, string? TimezoneOverlap);

/// <summary>The job facts sent to <c>job_fit_score</c>: enrichment JSON fields, never the raw description (PRD §7.5).</summary>
public sealed record FitJobFacts(
    string Title,
    string CompanyName,
    string? LocationText,
    string? WorkMode,
    string? EmploymentType,
    string Eligibility,
    string DescriptionCompleteness,
    EnrichmentOutput Enrichment);

/// <summary>
/// <c>job_fit_score</c>: 1–10 fit with reasons and skill lists, per user and job (PRD F6, §7.5). Charged to the user's
/// Discovery budget. The candidate summary is the stable, cached part of the prompt (built once per run).
/// </summary>
public static class JobFitScoreTask
{
    public const int MaxReasonWords = 20;
    public const int MaxReasons = 3;
    private const int MaxSkills = 80;
    private const int MaxSummaryChars = 600;

    public static readonly AiTask<FitScoreOutput> Definition = new(AiTaskNames.JobFitScore, Validate);

    /// <summary>
    /// A short profile summary (data minimisation, SDD §14.3): years, recent titles, skills, domains, the résumé's own
    /// summary and the fit-related preferences. No contact details or employers. Wrapped in <c>&lt;candidate_data&gt;</c>.
    /// </summary>
    public static string CandidateSummary(ParsedProfile profile, IReadOnlyList<string> confirmedSkills, FitPreferences prefs)
    {
        var sb = new StringBuilder();
        sb.Append("Total years of experience: ")
          .AppendLine(profile.TotalYears is { } y ? y.ToString("0.#", CultureInfo.InvariantCulture) : "not stated");
        var roles = profile.Experience.Take(4)
            .Select(r => $"{r.Title} ({r.Start ?? "?"} – {r.End ?? "present"})")
            .Where(s => s.Trim().Length > 0);
        sb.Append("Recent roles: ").AppendLine(string.Join("; ", roles) is { Length: > 0 } rr ? rr : "not stated");
        var skills = profile.Skills.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxSkills).ToList();
        sb.Append("Skills: ").AppendLine(skills.Count > 0 ? string.Join(", ", skills) : "not stated");
        var extra = confirmedSkills.Where(c => !skills.Contains(c, StringComparer.OrdinalIgnoreCase)).Take(40).ToList();
        if (extra.Count > 0) sb.Append("Skills the candidate confirmed: ").AppendLine(string.Join(", ", extra));
        if (profile.Domains.Count > 0) sb.Append("Domains: ").AppendLine(string.Join(", ", profile.Domains));
        if (!string.IsNullOrWhiteSpace(profile.Summary))
            sb.Append("Résumé summary: ").AppendLine(profile.Summary.Length > MaxSummaryChars ? profile.Summary[..MaxSummaryChars] : profile.Summary);
        sb.Append("Target roles: ").AppendLine(prefs.TargetRoles.Count > 0 ? string.Join(", ", prefs.TargetRoles) : "not stated");
        if (!string.IsNullOrWhiteSpace(prefs.ExperienceLevel)) sb.Append("Experience level sought: ").AppendLine(prefs.ExperienceLevel);
        sb.Append("Time-zone overlap preference: ").Append(string.IsNullOrWhiteSpace(prefs.TimezoneOverlap) ? "any" : prefs.TimezoneOverlap);
        return AiTaskRunner.BuildUserContent([new UntrustedBlock("candidate_data", sb.ToString())]);
    }

    public static AiTaskInput BuildInput(string candidateSummary, FitJobFacts job)
    {
        var e = job.Enrichment;
        var sb = new StringBuilder()
            .Append("Title: ").AppendLine(job.Title)
            .Append("Company: ").AppendLine(job.CompanyName)
            .Append("Location: ").AppendLine(job.LocationText ?? "not stated")
            .Append("Work mode: ").AppendLine(e.WorkMode ?? job.WorkMode ?? "not stated")
            .Append("Employment type: ").AppendLine(e.EmploymentType ?? job.EmploymentType ?? "not stated")
            .Append("Seniority: ").AppendLine(e.Seniority ?? "not stated")
            .Append("Years of experience asked: ").AppendLine(Years(e.YearsExperienceMin, e.YearsExperienceMax))
            .Append("Required skills: ").AppendLine(e.SkillsRequired.Count > 0 ? string.Join(", ", e.SkillsRequired) : "not stated")
            .Append("Preferred skills: ").AppendLine(e.SkillsPreferred.Count > 0 ? string.Join(", ", e.SkillsPreferred) : "none");
        if (e.SkillAliases.Count > 0)
            sb.Append("Skill aliases: ").AppendLine(string.Join("; ", e.SkillAliases.Select(a => $"{a.Skill} = {string.Join(", ", a.Aliases)}")));
        sb.Append("Time-zone requirement: ").AppendLine(e.TimezoneRequirement ?? "none stated")
          .Append("Hiring model: ").AppendLine(e.HiringModelHint)
          .Append("Open to candidates in India: ").AppendLine(job.Eligibility)
          .Append("Job summary: ").AppendLine(e.Summary)
          .Append("Description completeness: ").Append(job.DescriptionCompleteness);
        return new AiTaskInput([new UntrustedBlock("job_data", sb.ToString())], candidateSummary);
    }

    /// <summary>Deterministic checks the schema cannot express. Messages carry no content.</summary>
    internal static IReadOnlyList<string> Validate(FitScoreOutput f)
    {
        var errors = new List<string>();
        if (f.FitScore is < 1 or > 10) errors.Add("fit_score is outside 1–10.");
        if (f.Reasons.Count is < 1 or > MaxReasons) errors.Add($"reasons must have 1 to {MaxReasons} entries.");
        for (var i = 0; i < f.Reasons.Count; i++)
            if (JobEnrichmentTask.WordCount(f.Reasons[i]) > MaxReasonWords)
                errors.Add($"reasons[{i}] has more than {MaxReasonWords} words.");
        return errors;
    }

    /// <summary>Confidence is never "high" when the job description was limited (PRD §7.5 step 2).</summary>
    public static string CapConfidence(string confidence, string descriptionCompleteness) =>
        descriptionCompleteness != "full" && confidence == "high" ? "medium" : confidence;

    private static string Years(int? min, int? max) => (min, max) switch
    {
        (null, null) => "not stated",
        ({ } a, null) => $"{a}+",
        (null, { } b) => $"up to {b}",
        ({ } a, { } b) => $"{a}–{b}",
    };
}
