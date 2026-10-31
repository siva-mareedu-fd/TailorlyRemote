namespace Tailorly.Core.Ats;

/// <summary>
/// The ATS Match Score, 0–100, "estimated" (PRD §8.2, ADR-004):
/// <c>0.35·keyword_coverage + 0.25·requirements_match + 0.10·title_seniority + 0.15·experience_relevance + 0.15·format_health</c>,
/// weights from configuration. Deterministic: the model supplies only requirement statuses and two labels; the arithmetic
/// is here. A component without data (null) is left out and the remaining weights are rescaled, so a job that lists no
/// skills is not scored as if the résumé matched none of them.
/// </summary>
public static class AtsScoreCalculator
{
    public static AtsScore Combine(AtsComponents c, AtsWeights w)
    {
        double sum = 0, weights = 0;
        void Add(double? value, double weight)
        {
            if (value is not { } v || double.IsNaN(v) || weight <= 0) return;
            sum += Math.Clamp(v, 0, 1) * weight;
            weights += weight;
        }

        Add(c.KeywordCoverage, w.KeywordCoverage);
        Add(c.RequirementsMatch, w.RequirementsMatch);
        Add(c.TitleSeniority, w.TitleSeniority);
        Add(c.ExperienceRelevance, w.ExperienceRelevance);
        Add(c.FormatHealth, w.FormatHealth);
        // Rounded to 6 places first so binary floating point (58.4999…) does not decide a half.
        var score = weights == 0 ? 0 : (int)Math.Round(Math.Round(100 * sum / weights, 6), MidpointRounding.AwayFromZero);
        return new AtsScore(Math.Clamp(score, 0, 100), Clamped(c));
    }

    /// <summary>
    /// <c>requirements_match</c>: met = 1, partial = 0.5, missing = 0, weighted like keywords (required 1.0, preferred 0.5).
    /// Null when the analysis found no requirements.
    /// </summary>
    public static double? RequirementsMatch(IReadOnlyList<RequirementStatus> requirements)
    {
        double total = 0, met = 0;
        foreach (var r in requirements)
        {
            var weight = r.Type == RequirementTypes.Preferred ? KeywordCoverage.PreferredWeight : KeywordCoverage.RequiredWeight;
            total += weight;
            met += weight * r.Status switch
            {
                RequirementStatuses.Met => 1.0,
                RequirementStatuses.Partial => 0.5,
                _ => 0.0,
            };
        }
        return total == 0 ? null : met / total;
    }

    /// <summary><c>experience_relevance</c>: the model's 0–1 label, clamped; null if absent.</summary>
    public static double? ExperienceRelevance(double? label) => label is { } l && !double.IsNaN(l) ? Math.Clamp(l, 0, 1) : null;

    private static AtsComponents Clamped(AtsComponents c) => new(
        Round(c.KeywordCoverage), Round(c.RequirementsMatch), Round(c.TitleSeniority), Round(c.ExperienceRelevance), Round(c.FormatHealth));

    private static double? Round(double? v) => v is { } x && !double.IsNaN(x) ? Math.Round(Math.Clamp(x, 0, 1), 3) : null;
}
