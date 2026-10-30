using Tailorly.Core.Jobs;

namespace Tailorly.Core.Matching;

/// <summary>Who decided a job's final eligibility (stored in the enrichment JSON as <c>eligibility_source</c>).</summary>
public static class EligibilitySource
{
    public const string Source = "source";
    public const string Rules = "rules";
    public const string Model = "model";
}

public sealed record ResolvedEligibility(string Eligibility, string? Evidence, string Source);

/// <summary>
/// Final eligibility (SDD §8.6). Order: (1) structured source fields, (2) phrase rules, (3) the model, which receives
/// (1)–(2) as hints. The model's value is final <b>unless</b> a hard rule conflicts: a structured source field always
/// wins, and so does an explicit restriction that excludes India; the rule's evidence is kept.
/// </summary>
public static class EligibilityResolver
{
    private static readonly HashSet<string> Valid = new(StringComparer.Ordinal)
    {
        Eligibility.Worldwide, Eligibility.IndiaEligible, Eligibility.Restricted, Eligibility.Unclear,
    };

    public static ResolvedEligibility Resolve(EligibilityHint rules, string? modelEligibility, string? modelEvidence)
    {
        if (rules.Basis == EligibilityBasis.Structured)
            return new(rules.Eligibility, rules.Evidence, EligibilitySource.Source);
        if (rules.IsHard)
            return new(rules.Eligibility, rules.Evidence, EligibilitySource.Rules);
        if (modelEligibility is not null && Valid.Contains(modelEligibility))
            return new(modelEligibility, string.IsNullOrWhiteSpace(modelEvidence) ? null : modelEvidence.Trim(), EligibilitySource.Model);
        return new(rules.Eligibility, rules.Evidence, EligibilitySource.Rules);
    }

    /// <summary>
    /// Eligibility display rule (PRD §7.5 step 4, ApiDesign §7): restricted jobs are never shown; unclear jobs appear in
    /// the app with a warning and in the email only when the user opted in.
    /// </summary>
    public static bool Visible(string eligibility, bool inEmail, bool showUnclearInEmail) => eligibility switch
    {
        Eligibility.Restricted => false,
        Eligibility.Unclear => !inEmail || showUnclearInEmail,
        _ => true,
    };
}
