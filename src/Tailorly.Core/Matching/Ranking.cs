namespace Tailorly.Core.Matching;

/// <param name="RuleBased">True when the score comes from the rule-based fallback, not the model.</param>
public sealed record RankedItem(Guid JobId, int FitScore, bool RuleBased, DateTimeOffset? PostedAt, DateTimeOffset CreatedAt);

/// <summary>
/// Ranking (PRD §7.5 step 5): fit score descending, then recency (posted date, else first seen). Model-scored jobs come
/// before rule-ranked ones, whose scores are not comparable. The job id breaks ties so the order is stable.
/// </summary>
public static class Ranking
{
    public static IReadOnlyList<RankedItem> Order(IEnumerable<RankedItem> items) => items
        .OrderBy(i => i.RuleBased)
        .ThenByDescending(i => i.FitScore)
        .ThenByDescending(i => i.PostedAt ?? i.CreatedAt)
        .ThenBy(i => i.JobId)
        .ToList();

    /// <summary>
    /// Rule-based fallback score 1–10 when the Discovery budget is used up or a job could not be enriched (PRD F7):
    /// title/skill relevance from the raw prefilter plus the share of the job's required skills the user has. Only used
    /// for ordering; the API shows no fit score for these jobs.
    /// </summary>
    public static int RuleScore(double relevance, int matchedRequired, int required)
    {
        var coverage = required > 0 ? matchedRequired / (double)required : 0;
        var score = 1 + Math.Round(Math.Min(relevance, 4) * 1.25 + coverage * 4, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(score, 1, 10);
    }
}
