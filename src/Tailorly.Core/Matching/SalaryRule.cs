using Tailorly.Core.Jobs;

namespace Tailorly.Core.Matching;

/// <summary>Salary labels shown on every job (ApiDesign §2 <c>salaryLabel</c>).</summary>
public static class SalaryLabels
{
    public const string Stated = "stated";
    public const string Estimated = "estimated";
    public const string NotListed = "not_listed";

    public static string For(Salary s) =>
        s.Kind == SalaryKinds.Unknown || (s.Min is null && s.Max is null) ? NotListed
        : s.Kind == SalaryKinds.Estimated ? Estimated
        : Stated;
}

/// <param name="Keep">Whether the job stays in the shortlist.</param>
/// <param name="Label">stated | estimated | not_listed.</param>
/// <param name="Reason">Short reason code for counts and tests (no content).</param>
public sealed record SalaryDecision(bool Keep, string Label, string Reason);

/// <summary>
/// The salary rule, applied after scoring (PRD §7.5 step 3):
/// <list type="bullet">
/// <item>salary stated or estimated and within the user's range (after currency conversion) → keep;</item>
/// <item>salary stated and outside the range → drop;</item>
/// <item>salary unknown → keep only if <c>include_unspecified_salary</c> and <c>fit_score ≥ min_match_score</c>, tagged
/// "salary not listed".</item>
/// </list>
/// Cases the PRD does not name are kept, because only a stated salary is reliable enough to drop a job: an estimate
/// outside the range, a user without a range, and a currency that cannot be converted. "Within" means the job's range
/// overlaps the user's (an open end of either range is unbounded). A missing fit score (rule-based fallback digest) does
/// not block a job with an unknown salary.
/// </summary>
public static class SalaryRule
{
    public const string WithinRange = "within_range";
    public const string OutsideRange = "outside_range";
    public const string EstimateOutsideRange = "estimate_outside_range";
    public const string NoRange = "no_range";
    public const string NoRate = "no_rate";
    public const string UnspecifiedKept = "unspecified_kept";
    public const string UnspecifiedExcluded = "unspecified_excluded";
    public const string UnspecifiedBelowMinScore = "unspecified_below_min_score";

    /// <param name="rate">Units of the user's currency per unit of the job's currency, or null if unknown.</param>
    /// <param name="fitScore">The job's fit score, or null when the run fell back to rule-based ranking.</param>
    public static SalaryDecision Apply(Salary salary, MatchPreferences prefs, decimal? rate, int? fitScore)
    {
        var label = SalaryLabels.For(salary);
        if (label == SalaryLabels.NotListed)
        {
            if (!prefs.IncludeUnspecifiedSalary) return new(false, label, UnspecifiedExcluded);
            return fitScore is null || fitScore >= prefs.MinMatchScore
                ? new(true, label, UnspecifiedKept)
                : new(false, label, UnspecifiedBelowMinScore);
        }

        if (prefs.SalaryMin is null && prefs.SalaryMax is null) return new(true, label, NoRange);
        if (rate is not { } r || r <= 0) return new(true, label, NoRate);

        var lo = (salary.Min ?? salary.Max!.Value) * r;
        var hi = (salary.Max ?? salary.Min!.Value) * r;
        var within = (prefs.SalaryMin is not { } min || hi >= min) && (prefs.SalaryMax is not { } max || lo <= max);
        if (within) return new(true, label, WithinRange);
        return label == SalaryLabels.Stated ? new(false, label, OutsideRange) : new(true, label, EstimateOutsideRange);
    }
}
