using System.Text.Json.Serialization;
using Tailorly.Docx.Tailoring;

namespace Tailorly.App.Ai.Tasks;

/// <summary>Shape of the interview_prep reply (PRD Appendix B).</summary>
public sealed record InterviewPrepOutput(
    [property: JsonPropertyName("technical_topics")] IReadOnlyList<PrepTopic> TechnicalTopics,
    [property: JsonPropertyName("behavioural")] IReadOnlyList<PrepBehavioural> Behavioural,
    [property: JsonPropertyName("gaps_to_prepare")] IReadOnlyList<string> GapsToPrepare,
    [property: JsonPropertyName("questions_to_ask")] IReadOnlyList<string> QuestionsToAsk);

public sealed record PrepTopic(
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("why")] string Why);

public sealed record PrepBehavioural(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("talking_points_from_resume")] IReadOnlyList<string> TalkingPoints);

/// <param name="Resume">Résumé, profile and confirmed skills: the only allowed source of talking points.</param>
/// <param name="ResumeAndJob">The résumé facts plus the job posting: the allowed source of topics and gaps.</param>
public sealed record InterviewPrepRules(FactSource Resume, FactSource ResumeAndJob);

/// <summary>
/// <c>interview_prep</c> (PRD F10): likely technical topics, behavioural questions with talking points drawn only from
/// the candidate's real experience, gaps to prepare and questions to ask. Charged to Documents. The truthfulness checks
/// are tailoring's: a talking point may not state a number or name a term (employer, product, skill, credential) that is
/// not in the résumé; topics and gaps may also use the job posting's terms. A violation goes back in the one repair turn.
/// </summary>
public static class InterviewPrepTask
{
    public const int MaxDescriptionChars = 6_000;

    public static AiTask<InterviewPrepOutput> For(InterviewPrepRules rules) => new(AiTaskNames.InterviewPrep, o => Validate(o, rules));

    public static AiTaskInput BuildInput(TailoringJob job, ParsedProfile profile, IReadOnlyList<string> confirmed) =>
        new([new UntrustedBlock("job_data", AtsAnalysisTask.JobBlock(job, MaxDescriptionChars))], CoverLetterTask.CandidateBlock(profile, confirmed));

    /// <summary>Deterministic checks. Messages name fields by position, never their text.</summary>
    internal static IReadOnlyList<string> Validate(InterviewPrepOutput o, InterviewPrepRules r)
    {
        var errors = new List<string>();
        for (var i = 0; i < o.TechnicalTopics.Count; i++)
        {
            Plain(errors, $"technical_topics[{i}].topic", o.TechnicalTopics[i].Topic);
            Plain(errors, $"technical_topics[{i}].why", o.TechnicalTopics[i].Why);
            Grounded(errors, $"technical_topics[{i}]", $"{o.TechnicalTopics[i].Topic}\n{o.TechnicalTopics[i].Why}", r.ResumeAndJob,
                "the résumé or the job posting");
        }
        for (var i = 0; i < o.Behavioural.Count; i++)
        {
            Plain(errors, $"behavioural[{i}].question", o.Behavioural[i].Question);
            for (var j = 0; j < o.Behavioural[i].TalkingPoints.Count; j++)
            {
                var at = $"behavioural[{i}].talking_points_from_resume[{j}]";
                Plain(errors, at, o.Behavioural[i].TalkingPoints[j]);
                Grounded(errors, at, o.Behavioural[i].TalkingPoints[j], r.Resume, "the résumé");
            }
        }
        for (var i = 0; i < o.GapsToPrepare.Count; i++)
        {
            Plain(errors, $"gaps_to_prepare[{i}]", o.GapsToPrepare[i]);
            Grounded(errors, $"gaps_to_prepare[{i}]", o.GapsToPrepare[i], r.ResumeAndJob, "the job posting");
        }
        for (var i = 0; i < o.QuestionsToAsk.Count; i++) Plain(errors, $"questions_to_ask[{i}]", o.QuestionsToAsk[i]);
        return errors;
    }

    private static void Plain(List<string> errors, string at, string text)
    {
        if (FactGuard.HasMarkdown(text)) errors.Add($"{at} uses markdown or a bullet character; write plain text.");
        if (FactGuard.Pictographs(text).Count > 0) errors.Add($"{at} contains an emoji or pictograph.");
        if (FactGuard.Links(text).Count > 0) errors.Add($"{at} contains a link or email address.");
    }

    private static void Grounded(List<string> errors, string at, string text, FactSource facts, string where)
    {
        if (FactGuard.NewNumbers(text, facts).Count > 0)
            errors.Add($"{at} states a number that is not in {where}; use only those numbers.");
        if (FactGuard.NewTerms(text, facts, []).Count > 0)
            errors.Add($"{at} names an employer, product, title, credential or skill that is not in {where}.");
    }
}
