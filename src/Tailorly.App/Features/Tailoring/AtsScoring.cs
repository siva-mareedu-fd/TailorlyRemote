using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Ats;
using Tailorly.Docx;

namespace Tailorly.App.Features.Tailoring;

/// <param name="Format">Format health with its findings (deterministic, from the layout).</param>
public sealed record AtsResult(AtsScore Score, KeywordCoverageResult Keywords, FormatHealthResult Format);

/// <summary>
/// The ATS Match Score of a résumé for a job (PRD §8.2): Core's calculator fed with the extracted résumé (keywords,
/// format health), the profile (titles and years) and the model's requirement statuses and labels. The projection while
/// edits are proposed recomputes only the keyword component; the rest comes from the analysis of the original.
/// </summary>
public sealed partial class AtsScoring(IOptions<AtsWeights> weights)
{
    public const string Disclaimer = "Estimated; real ATS systems vary.";

    public AtsResult Score(DocModel resume, ParsedProfile? profile, EnrichmentOutput? enrichment, AtsAnalysisOutput analysis)
    {
        var keywords = KeywordCoverage.Compute(Skills(enrichment), Lines(resume));
        var format = FormatHealth.Evaluate(Format(resume));
        var seniority = new SeniorityInput(enrichment?.Seniority, enrichment?.YearsExperienceMin, enrichment?.YearsExperienceMax,
            profile?.Experience.Select(r => r.Title).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [], profile?.TotalYears,
            analysis.TitleSeniorityLabel);
        var components = new AtsComponents(
            keywords.Score,
            AtsScoreCalculator.RequirementsMatch(analysis.Requirements.Select(r => new RequirementStatus(r.Type, r.Status)).ToList()),
            TitleSeniority.Compute(seniority),
            AtsScoreCalculator.ExperienceRelevance(analysis.ExperienceRelevance.Score),
            format.Score);
        return new AtsResult(AtsScoreCalculator.Combine(components, weights.Value), keywords, format);
    }

    /// <summary>
    /// The score with some paragraphs replaced: keyword coverage on the new text, every other component as before (the
    /// layout, titles and the model's judgements do not change until the final text is re-scored).
    /// </summary>
    public (AtsScore Score, KeywordCoverageResult Keywords) Project(DocModel resume, IReadOnlyDictionary<string, string> replaced,
        EnrichmentOutput? enrichment, AtsComponentsDto before)
    {
        var lines = resume.HeaderFooterLines.Concat(resume.Paragraphs.Select(p => replaced.GetValueOrDefault(p.Id) ?? p.Text));
        var keywords = KeywordCoverage.Compute(Skills(enrichment), lines);
        var components = new AtsComponents(keywords.Score, before.RequirementsMatch, before.TitleSeniority, before.ExperienceRelevance,
            before.FormatHealth);
        return (AtsScoreCalculator.Combine(components, weights.Value), keywords);
    }

    public static JobSkillSet Skills(EnrichmentOutput? e)
    {
        if (e is null) return new JobSkillSet([], []);
        var aliases = e.SkillAliases.GroupBy(a => a.Skill, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.SelectMany(a => a.Aliases).ToList(), StringComparer.OrdinalIgnoreCase);
        SkillTerm Term(string s) => new(s, aliases.GetValueOrDefault(s) ?? []);
        return new JobSkillSet(e.SkillsRequired.Select(Term).ToList(), e.SkillsPreferred.Select(Term).ToList());
    }

    public static IReadOnlyList<string> Lines(DocModel resume) => resume.HeaderFooterLines.Concat(resume.Paragraphs.Select(p => p.Text)).ToList();

    /// <summary>Layout facts for format health: the upload-time findings plus headings and contact details from the text.</summary>
    public static FormatInputs Format(DocModel resume)
    {
        var f = resume.Findings;
        var sections = resume.Paragraphs.Select(p => p.Section).ToHashSet(StringComparer.Ordinal);
        var body = string.Join("\n", resume.Paragraphs.Select(p => p.Text));
        return new FormatInputs(f.HasTables, f.HasColumns, f.HasTextBoxes, f.HasImages, f.HeaderFooterText, f.HeaderFooterContact,
            f.Fonts, f.WordCount, f.PageEstimate, f.TrackedChanges,
            HasStandardHeadings: sections.Contains(ResumeSections.Experience) && sections.Contains(ResumeSections.Skills),
            HasContactDetails: Email().IsMatch(body) || Phone().IsMatch(body));
    }

    /// <summary>
    /// The skill-confirmation candidates (PRD §8.3 step 2): the model's gaps that the résumé really does not show, plus the
    /// job's required skills the keyword check did not find, without duplicates.
    /// </summary>
    public static IReadOnlyList<StoredGap> Gaps(AtsAnalysisOutput analysis, KeywordCoverageResult keywords, IReadOnlyList<string> lines)
    {
        var gaps = new List<StoredGap>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in analysis.Gaps)
        {
            var key = KeywordCoverage.Key(g.Skill);
            if (key.Length == 0 || KeywordCoverage.Mentions(new SkillTerm(g.Skill, []), lines) || !seen.Add(key)) continue;
            gaps.Add(new StoredGap(g.Skill.Trim(), g.LikelyHasBasedOnProfile));
        }
        foreach (var s in keywords.MissingRequired)
            if (seen.Add(KeywordCoverage.Key(s))) gaps.Add(new StoredGap(s, false));
        return gaps.Take(15).ToList();
    }

    public static AnalysisDto ToDto(AtsAnalysis a, IReadOnlySet<string> confirmedKeys)
    {
        var score = TailoringJson.Read<StoredScore>(a.Components);
        var requirements = TailoringJson.Read<List<StoredRequirement>>(a.Requirements) ?? [];
        var gaps = TailoringJson.Read<List<StoredGap>>(a.Gaps) ?? [];
        var findings = TailoringJson.Read<List<FindingDto>>(a.Findings) ?? [];
        return new AnalysisDto(a.Id, a.UserJobId, a.ResumeId, a.Status, a.ErrorCode, a.AtsScore, score?.Components,
            requirements.Select(r => r.ToDto()).ToList(),
            gaps.Select(g => new GapDto(g.Skill, g.LikelyHas, confirmedKeys.Contains(UserConfirmedSkill.KeyOf(g.Skill)))).ToList(),
            findings, score?.Keywords, Disclaimer, a.UpdatedAt);
    }

    [GeneratedRegex(@"[^\s@]+@[^\s@]+\.[a-z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex Email();

    [GeneratedRegex(@"\+?\d[\d\s().-]{8,}\d")]
    private static partial Regex Phone();
}

/// <summary><c>ats_analyses.requirements</c> items (DatabaseDesign §4: <c>{text, type, status, evidencePids}</c>).</summary>
public sealed record StoredRequirement(string Text, string Type, string Status, IReadOnlyList<string> EvidencePids)
{
    public static StoredRequirement From(AtsRequirementOutput r) => new(r.Text.Trim(), r.Type, r.Status, r.EvidenceParagraphIds);

    public RequirementDto ToDto() => new(Text, Type, Status, EvidencePids);
}
