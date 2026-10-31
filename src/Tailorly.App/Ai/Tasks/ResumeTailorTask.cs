using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Tailorly.Docx;
using Tailorly.Docx.Tailoring;

namespace Tailorly.App.Ai.Tasks;

// Shape of the resume_tailor reply (PRD Appendix B).

public sealed record TailorEditOutput(
    [property: JsonPropertyName("paragraph_id")] string ParagraphId,
    [property: JsonPropertyName("new_text")] string NewText,
    [property: JsonPropertyName("rationale")] string Rationale,
    [property: JsonPropertyName("uses_confirmed_fact")] bool UsesConfirmedFact);

public sealed record TailorOutput(
    [property: JsonPropertyName("edits")] IReadOnlyList<TailorEditOutput> Edits,
    [property: JsonPropertyName("unaddressed_gaps")] IReadOnlyList<string> UnaddressedGaps);

/// <summary>What the analysis found the résumé shows weakly or not at all (the tailoring's targets).</summary>
/// <param name="WeakRequirements">Requirements marked missing or partial.</param>
/// <param name="MissingKeywords">Job skills not found in the résumé text.</param>
/// <param name="Gaps">Skill gaps with whether the profile makes them plausible.</param>
public sealed record TailorTargets(IReadOnlyList<string> WeakRequirements, IReadOnlyList<string> MissingKeywords,
    IReadOnlyList<(string Skill, bool LikelyHas)> Gaps);

/// <summary>
/// <c>resume_tailor</c> (PRD §8.3 step 4, Appendix B): edit-only JSON against paragraph ids, never a regenerated
/// document. The domain validator is <see cref="EditValidator.Violations"/> bound to this tailoring (locked or unknown
/// paragraphs, the cap, invented numbers, links, markdown…): the model gets one repair turn, then the task fails cleanly.
/// Charged to Documents. The paragraph listing is the cacheable part of the prompt.
/// </summary>
public static class ResumeTailorTask
{
    public const int MaxDescriptionChars = 8_000;
    public const int MaxRationaleWords = 40;

    public static AiTask<TailorOutput> For(TailorContext ctx) => new(AiTaskNames.ResumeTailor, o => Validate(o, ctx));

    public static IReadOnlyList<ProposedEdit> Proposed(TailorOutput o) =>
        o.Edits.Select(e => new ProposedEdit(e.ParagraphId, e.NewText, e.Rationale, e.UsesConfirmedFact)).ToList();

    public static AiTaskInput BuildInput(TailoringJob job, DocModel resume, TailorTargets targets, IReadOnlyList<string> confirmed,
        TailorRules rules)
    {
        var analysis = new StringBuilder()
            .Append("Requirements shown weakly or not at all: ")
            .AppendLine(targets.WeakRequirements.Count > 0 ? string.Join("; ", targets.WeakRequirements) : "none")
            .Append("Job skills not found in the résumé: ")
            .AppendLine(targets.MissingKeywords.Count > 0 ? string.Join(", ", targets.MissingKeywords) : "none")
            .Append("Skill gaps: ")
            .Append(targets.Gaps.Count > 0
                ? string.Join(", ", targets.Gaps.Select(g => g.Skill + (g.LikelyHas ? " (plausible from the profile, not confirmed)" : "")))
                : "none");
        var limits = new StringBuilder()
            .Append("Maximum number of edits: ").AppendLine(rules.MaxEdits.ToString(CultureInfo.InvariantCulture))
            .Append("Maximum length ratio (new text to original paragraph): ")
            .Append(rules.MaxLengthRatio.ToString("0.##", CultureInfo.InvariantCulture));
        return new AiTaskInput(
        [
            new UntrustedBlock("job_data", AtsAnalysisTask.JobBlock(job, MaxDescriptionChars)),
            new UntrustedBlock("analysis_data", analysis.ToString()),
            new UntrustedBlock("confirmed_skills_data", confirmed.Count > 0 ? string.Join(", ", confirmed) : "none"),
            new UntrustedBlock("limits_data", limits.ToString()),
        ], ResumeBlock(resume));
    }

    /// <summary>
    /// <c>id | section | editable | prefix "…" | text</c> per non-empty paragraph. The text of an editable paragraph is the part
    /// after its locked prefix (the model returns only that part).
    /// </summary>
    public static string ResumeBlock(DocModel resume)
    {
        var sb = new StringBuilder();
        foreach (var p in resume.Paragraphs)
        {
            var text = ResumeExtractor.Normalize(p.Text);
            if (text.Length == 0) continue;
            sb.Append(p.Id).Append(" | ").Append(p.Section).Append(" | ");
            if (p.Flags.Editable)
            {
                sb.Append("editable | ");
                var prefix = p.Text[..p.Flags.LockedPrefixLength];
                if (prefix.Trim().Length > 0) sb.Append("prefix \"").Append(ResumeExtractor.Normalize(prefix)).Append("\" | ");
                sb.AppendLine(ResumeExtractor.Normalize(p.Text[p.Flags.LockedPrefixLength..]));
            }
            else
            {
                sb.Append("locked | ").AppendLine(text);
            }
        }
        return AiTaskRunner.BuildUserContent([new UntrustedBlock("resume_data", sb.ToString().TrimEnd())]);
    }

    internal static IReadOnlyList<string> Validate(TailorOutput o, TailorContext ctx)
    {
        var errors = EditValidator.Violations(Proposed(o), ctx).ToList();
        for (var i = 0; i < o.Edits.Count; i++)
            if (JobEnrichmentTask.WordCount(o.Edits[i].Rationale) > MaxRationaleWords)
                errors.Add($"edits[{i}].rationale has more than {MaxRationaleWords} words.");
        return errors;
    }
}
