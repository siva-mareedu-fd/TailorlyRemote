using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Tailorly.Docx.Tailoring;

namespace Tailorly.App.Ai.Tasks;

/// <summary>Shape of the cover_letter reply (PRD Appendix B).</summary>
public sealed record CoverLetterOutput(
    [property: JsonPropertyName("paragraphs")] IReadOnlyList<string> Paragraphs,
    [property: JsonPropertyName("word_count")] int WordCount,
    [property: JsonPropertyName("tone")] string Tone);

/// <summary>What one letter is checked against.</summary>
/// <param name="Facts">Résumé, profile, confirmed skills, the job posting and the user's notes: the only allowed sources.</param>
public sealed record CoverLetterRules(string Tone, int MaxWords, IReadOnlyList<string> Cliches, string Company, string Title,
    FactSource Facts);

/// <summary>
/// <c>cover_letter</c> (PRD F9, §8.4): plain paragraphs in the requested tone within its word limit, an opening specific
/// to the company or role, none of the configured clichés, and the same truthfulness checks as tailoring: no number,
/// name, link or credential that is not in the résumé, the posting or the user's notes. Charged to Documents.
/// </summary>
public static class CoverLetterTask
{
    public const int MaxDescriptionChars = 6_000;

    /// <summary>Words a letter uses that need no source (greetings, sign-offs, the reader).</summary>
    public const string LetterVocabulary = "Dear Hello Hi Hiring Manager Recruiter Team Recruitment Talent Acquisition Regards Best Kind Warm " +
                                           "Sincerely Yours Thank You Thanks Cheers";

    public static AiTask<CoverLetterOutput> For(CoverLetterRules rules) => new(AiTaskNames.CoverLetter, o => Validate(o, rules));

    public static AiTaskInput BuildInput(TailoringJob job, ParsedProfile profile, IReadOnlyList<string> confirmed,
        IReadOnlyList<string> highlights, string? notes, CoverLetterRules rules)
    {
        var blocks = new List<UntrustedBlock>
        {
            new("job_data", AtsAnalysisTask.JobBlock(job, MaxDescriptionChars)),
        };
        if (highlights.Count > 0) blocks.Add(new UntrustedBlock("highlights_data", string.Join("\n", highlights)));
        if (!string.IsNullOrWhiteSpace(notes)) blocks.Add(new UntrustedBlock("notes_data", notes.Trim()));
        blocks.Add(new UntrustedBlock("style_data",
            $"Tone: {rules.Tone}\nWord limit: {rules.MaxWords.ToString(CultureInfo.InvariantCulture)}\n" +
            $"Phrases to avoid: {(rules.Cliches.Count > 0 ? string.Join("; ", rules.Cliches) : "none")}"));
        return new AiTaskInput(blocks, CandidateBlock(profile, confirmed));
    }

    /// <summary>The candidate's résumé facts (cacheable): name, summary, roles with bullets, skills, education, certifications.</summary>
    public static string CandidateBlock(ParsedProfile p, IReadOnlyList<string> confirmed)
    {
        var sb = new StringBuilder();
        sb.Append("Name: ").AppendLine(p.Contact?.Name ?? "not stated");
        if (p.TotalYears is { } y) sb.Append("Total years of experience: ").AppendLine(y.ToString("0.#", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(p.Summary)) sb.Append("Summary: ").AppendLine(p.Summary);
        foreach (var r in p.Experience)
        {
            sb.Append("Role: ").Append(r.Title).Append(" at ").Append(r.Company)
              .Append(" (").Append(r.Start ?? "?").Append(" – ").Append(r.End ?? "present").AppendLine(")");
            foreach (var b in r.Bullets) sb.Append("- ").AppendLine(b);
        }
        if (p.Skills.Count > 0) sb.Append("Skills: ").AppendLine(string.Join(", ", p.Skills.Select(s => s.Name)));
        if (confirmed.Count > 0) sb.Append("Skills the candidate confirmed: ").AppendLine(string.Join(", ", confirmed));
        foreach (var e in p.Education)
            sb.Append("Education: ").AppendLine(string.Join(", ", new[] { e.Degree, e.Field, e.Institution, e.End }.Where(x => x is not null)));
        foreach (var c in p.Certifications)
            sb.Append("Certification: ").AppendLine(string.Join(", ", new[] { c.Name, c.Issuer, c.Year }.Where(x => x is not null)));
        return AiTaskRunner.BuildUserContent([new UntrustedBlock("candidate_data", sb.ToString().TrimEnd())]);
    }

    public static int WordCount(IEnumerable<string> paragraphs) => paragraphs.Sum(JobEnrichmentTask.WordCount);

    /// <summary>Deterministic checks. Messages name paragraphs by position and configured phrases only, never letter text.</summary>
    internal static IReadOnlyList<string> Validate(CoverLetterOutput o, CoverLetterRules r)
    {
        var errors = new List<string>();
        if (o.Tone != r.Tone) errors.Add($"tone must be \"{r.Tone}\".");
        var words = WordCount(o.Paragraphs);
        if (words > r.MaxWords) errors.Add($"The letter has {words} words; the limit for a {r.Tone} letter is {r.MaxWords}. Shorten it.");

        var opening = string.Join(" ", o.Paragraphs.Take(2));
        if (!opening.Contains(r.Company, StringComparison.OrdinalIgnoreCase) && !opening.Contains(r.Title, StringComparison.OrdinalIgnoreCase))
            errors.Add("The opening must name the company or the role.");

        var all = string.Join("\n", o.Paragraphs);
        foreach (var cliche in r.Cliches.Where(c => c.Length > 0 && all.Contains(c, StringComparison.OrdinalIgnoreCase)))
            errors.Add($"Avoid the phrase \"{cliche}\".");

        for (var i = 0; i < o.Paragraphs.Count; i++)
        {
            var p = o.Paragraphs[i];
            var at = $"paragraphs[{i}]";
            if (p.Contains('\n') || p.Contains('\r')) errors.Add($"{at} contains a line break; give each paragraph as its own entry.");
            if (FactGuard.HasMarkdown(p)) errors.Add($"{at} uses markdown or a bullet character; write plain paragraphs.");
            if (FactGuard.Pictographs(p).Count > 0) errors.Add($"{at} contains an emoji or pictograph.");
            if (FactGuard.Links(p).Any(l => !r.Facts.HasText(l))) errors.Add($"{at} contains a link or email address that is not in the résumé.");
            if (p.Contains('[', StringComparison.Ordinal) && p.Contains(']', StringComparison.Ordinal)) errors.Add($"{at} contains a placeholder in brackets.");
            if (FactGuard.NewNumbers(p, r.Facts).Count > 0)
                errors.Add($"{at} states a number that is not in the résumé, the job posting or the notes; use only those numbers.");
            if (FactGuard.NewTerms(p, r.Facts, []).Count > 0)
                errors.Add($"{at} names an employer, product, title, credential or skill that is not in the résumé, the job posting or the notes.");
        }
        return errors;
    }
}
