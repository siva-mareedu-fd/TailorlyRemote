using System.Text;
using System.Text.Json.Serialization;
using Tailorly.Docx;

namespace Tailorly.App.Ai.Tasks;

// Shape of the ats_analysis reply (PRD Appendix B; keys snake_case as there).

public sealed record AtsRequirementOutput(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("evidence_paragraph_ids")] IReadOnlyList<string> EvidenceParagraphIds);

public sealed record ExperienceRelevanceOutput(
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("justification")] string Justification);

public sealed record AtsGapOutput(
    [property: JsonPropertyName("skill")] string Skill,
    [property: JsonPropertyName("likely_has_based_on_profile")] bool LikelyHasBasedOnProfile);

public sealed record AtsAnalysisOutput(
    [property: JsonPropertyName("requirements")] IReadOnlyList<AtsRequirementOutput> Requirements,
    [property: JsonPropertyName("title_seniority_label")] double TitleSeniorityLabel,
    [property: JsonPropertyName("experience_relevance")] ExperienceRelevanceOutput ExperienceRelevance,
    [property: JsonPropertyName("gaps")] IReadOnlyList<AtsGapOutput> Gaps);

/// <summary>The job a tracked entry points at: its own listing or the description the user pasted, with its enrichment.</summary>
/// <param name="Enrichment">The listing's shared enrichment, or the enrichment of the pasted text; null if none could be made.</param>
public sealed record TailoringJob(string Title, string CompanyName, string Description, EnrichmentOutput? Enrichment);

/// <summary>
/// <c>ats_analysis</c> (PRD §8.2–8.3): requirement statuses with evidence paragraph ids, the title/seniority and
/// experience-relevance labels and the skill gaps. The score itself is computed deterministically from these
/// (ADR-004). Charged to Documents. The résumé is the cacheable part of the prompt.
/// </summary>
public static class AtsAnalysisTask
{
    public const int MaxDescriptionChars = 10_000;
    public const int MaxJustificationWords = 40;

    /// <summary>The task with the domain validator bound to this résumé's paragraph ids.</summary>
    public static AiTask<AtsAnalysisOutput> For(DocModel resume)
    {
        var ids = resume.Paragraphs.Where(p => ResumeExtractor.Normalize(p.Text).Length > 0).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return new AiTask<AtsAnalysisOutput>(AiTaskNames.AtsAnalysis, o => Validate(o, ids));
    }

    /// <summary>The job in <c>&lt;job_data&gt;</c>; the résumé (cacheable) in <c>&lt;resume_data&gt;</c>.</summary>
    public static AiTaskInput BuildInput(TailoringJob job, DocModel resume) =>
        new([new UntrustedBlock("job_data", JobBlock(job, MaxDescriptionChars))], ResumeBlock(resume));

    /// <summary>One line per non-empty paragraph: <c>[p12] (experience) text</c>; header/footer lines first.</summary>
    public static string ResumeBlock(DocModel resume)
    {
        var sb = new StringBuilder();
        foreach (var line in resume.HeaderFooterLines) sb.Append("[header/footer] ").AppendLine(line);
        foreach (var p in resume.Paragraphs)
        {
            var text = ResumeExtractor.Normalize(p.Text);
            if (text.Length == 0) continue;
            sb.Append('[').Append(p.Id).Append("] (").Append(p.Section).Append(") ").AppendLine(text);
        }
        return AiTaskRunner.BuildUserContent([new UntrustedBlock("resume_data", sb.ToString().TrimEnd())]);
    }

    /// <summary>Job facts and (truncated) description, shared by the tailoring tasks.</summary>
    public static string JobBlock(TailoringJob job, int maxDescriptionChars)
    {
        var e = job.Enrichment;
        var sb = new StringBuilder()
            .Append("Title: ").AppendLine(job.Title)
            .Append("Company: ").AppendLine(job.CompanyName);
        if (e is not null)
        {
            sb.Append("Seniority: ").AppendLine(e.Seniority ?? "not stated")
              .Append("Years of experience asked: ").AppendLine(Years(e.YearsExperienceMin, e.YearsExperienceMax))
              .Append("Required skills: ").AppendLine(e.SkillsRequired.Count > 0 ? string.Join(", ", e.SkillsRequired) : "not stated")
              .Append("Preferred skills: ").AppendLine(e.SkillsPreferred.Count > 0 ? string.Join(", ", e.SkillsPreferred) : "none");
            if (e.SkillAliases.Count > 0)
                sb.Append("Skill aliases: ").AppendLine(string.Join("; ", e.SkillAliases.Select(a => $"{a.Skill} = {string.Join(", ", a.Aliases)}")));
            sb.Append("Summary: ").AppendLine(e.Summary);
        }
        var description = job.Description.Length > maxDescriptionChars ? job.Description[..maxDescriptionChars] : job.Description;
        sb.AppendLine("Description:").Append(description);
        return sb.ToString();
    }

    /// <summary>Deterministic checks the schema cannot express. Messages carry ids and positions only.</summary>
    internal static IReadOnlyList<string> Validate(AtsAnalysisOutput o, IReadOnlySet<string> ids)
    {
        var errors = new List<string>();
        for (var i = 0; i < o.Requirements.Count; i++)
        {
            var r = o.Requirements[i];
            if (string.IsNullOrWhiteSpace(r.Text)) errors.Add($"requirements[{i}].text is blank.");
            var unknown = r.EvidenceParagraphIds.Where(id => !ids.Contains(id)).ToList();
            if (unknown.Count > 0) errors.Add($"requirements[{i}] cites paragraph ids that are not in the résumé data.");
            if (r.Status is "met" or "partial" && r.EvidenceParagraphIds.Count(ids.Contains) == 0)
                errors.Add($"requirements[{i}] is {r.Status} but cites no résumé paragraph; cite the paragraphs or mark it missing.");
        }
        if (JobEnrichmentTask.WordCount(o.ExperienceRelevance.Justification) > MaxJustificationWords)
            errors.Add($"experience_relevance.justification has more than {MaxJustificationWords} words.");
        for (var i = 0; i < o.Gaps.Count; i++)
            if (string.IsNullOrWhiteSpace(o.Gaps[i].Skill)) errors.Add($"gaps[{i}].skill is blank.");
        return errors;
    }

    private static string Years(int? min, int? max) => (min, max) switch
    {
        (null, null) => "not stated",
        ({ } a, null) => $"{a}+",
        (null, { } b) => $"up to {b}",
        ({ } a, { } b) => $"{a}–{b}",
    };
}
