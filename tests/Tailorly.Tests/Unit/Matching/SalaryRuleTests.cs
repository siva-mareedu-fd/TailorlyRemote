using FluentAssertions;
using Tailorly.Core.Jobs;
using Tailorly.Core.Matching;

namespace Tailorly.Tests.Unit.Matching;

/// <summary>
/// The salary rule truth table (PRD §7.5 step 3). Preferences are INR 30L–55L unless a row says otherwise; job salaries
/// are in USD at 95 INR/USD (so $40k ≈ 38L, $70k ≈ 66.5L, $20k ≈ 19L).
/// </summary>
public sealed class SalaryRuleTests
{
    private const decimal UsdInr = 95m;

    private static MatchPreferences Prefs(long? min = 3_000_000, long? max = 5_500_000, bool includeUnspecified = true, int minScore = 8,
        string currency = "INR") =>
        new() { SalaryMin = min, SalaryMax = max, IncludeUnspecifiedSalary = includeUnspecified, MinMatchScore = minScore, SalaryCurrency = currency };

    private static Salary Usd(long? min, long? max, string kind = SalaryKinds.Stated) => new(min, max, "USD", "year", kind);

    public static TheoryData<string, Salary, MatchPreferences, decimal?, int?, bool, string, string> Cases => new()
    {
        // case, job salary, preferences, rate, fit, keep, label, reason
        { "stated, inside range", Usd(40_000, 50_000), Prefs(), UsdInr, 8, true, SalaryLabels.Stated, SalaryRule.WithinRange },
        { "stated, overlapping the minimum", Usd(20_000, 40_000), Prefs(), UsdInr, 8, true, SalaryLabels.Stated, SalaryRule.WithinRange },
        { "stated, overlapping the maximum", Usd(50_000, 80_000), Prefs(), UsdInr, 8, true, SalaryLabels.Stated, SalaryRule.WithinRange },
        { "stated, entirely below the minimum", Usd(10_000, 20_000), Prefs(), UsdInr, 9, false, SalaryLabels.Stated, SalaryRule.OutsideRange },
        { "stated, entirely above the maximum", Usd(70_000, 90_000), Prefs(), UsdInr, 9, false, SalaryLabels.Stated, SalaryRule.OutsideRange },
        { "stated, single amount inside", Usd(40_000, null), Prefs(), UsdInr, 8, true, SalaryLabels.Stated, SalaryRule.WithinRange },
        { "stated, single amount below", Usd(null, 20_000), Prefs(), UsdInr, 8, false, SalaryLabels.Stated, SalaryRule.OutsideRange },
        { "stated, user has only a minimum", Usd(70_000, 90_000), Prefs(max: null), UsdInr, 8, true, SalaryLabels.Stated, SalaryRule.WithinRange },
        { "stated, below a minimum-only range", Usd(10_000, 20_000), Prefs(max: null), UsdInr, 8, false, SalaryLabels.Stated, SalaryRule.OutsideRange },
        { "stated, user has only a maximum", Usd(10_000, 20_000), Prefs(min: null), UsdInr, 8, true, SalaryLabels.Stated, SalaryRule.WithinRange },
        { "stated, user has no range", Usd(10_000, 20_000), Prefs(min: null, max: null), UsdInr, 8, true, SalaryLabels.Stated, SalaryRule.NoRange },
        { "stated, no FX rate", Usd(10_000, 20_000), Prefs(), null, 8, true, SalaryLabels.Stated, SalaryRule.NoRate },
        { "stated, same currency", new Salary(3_500_000, 4_000_000, "INR", "year", SalaryKinds.Stated), Prefs(), 1m, 8, true, SalaryLabels.Stated, SalaryRule.WithinRange },
        { "stated in USD, user range in USD", Usd(40_000, 50_000), Prefs(min: 30_000, max: 60_000, currency: "USD"), 1m, 8, true, SalaryLabels.Stated, SalaryRule.WithinRange },
        { "estimated, inside range", Usd(40_000, 50_000, SalaryKinds.Estimated), Prefs(), UsdInr, 8, true, SalaryLabels.Estimated, SalaryRule.WithinRange },
        { "estimated, outside range is kept", Usd(10_000, 20_000, SalaryKinds.Estimated), Prefs(), UsdInr, 8, true, SalaryLabels.Estimated, SalaryRule.EstimateOutsideRange },
        { "unknown, included, fit at minimum", Salary.None, Prefs(), UsdInr, 8, true, SalaryLabels.NotListed, SalaryRule.UnspecifiedKept },
        { "unknown, included, fit above minimum", Salary.None, Prefs(), UsdInr, 10, true, SalaryLabels.NotListed, SalaryRule.UnspecifiedKept },
        { "unknown, included, fit below minimum", Salary.None, Prefs(), UsdInr, 7, false, SalaryLabels.NotListed, SalaryRule.UnspecifiedBelowMinScore },
        { "unknown, excluded by preference", Salary.None, Prefs(includeUnspecified: false), UsdInr, 10, false, SalaryLabels.NotListed, SalaryRule.UnspecifiedExcluded },
        { "unknown, rule-ranked (no fit score)", Salary.None, Prefs(), UsdInr, null, true, SalaryLabels.NotListed, SalaryRule.UnspecifiedKept },
        { "unknown, rule-ranked, excluded by preference", Salary.None, Prefs(includeUnspecified: false), UsdInr, null, false, SalaryLabels.NotListed, SalaryRule.UnspecifiedExcluded },
        { "kind stated but no amounts counts as not listed", new Salary(null, null, "USD", "year", SalaryKinds.Stated), Prefs(), UsdInr, 9, true, SalaryLabels.NotListed, SalaryRule.UnspecifiedKept },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Truth_table(string name, Salary salary, MatchPreferences prefs, decimal? rate, int? fit, bool keep, string label, string reason)
    {
        var d = SalaryRule.Apply(salary, prefs, rate, fit);
        d.Keep.Should().Be(keep, name);
        d.Label.Should().Be(label, name);
        d.Reason.Should().Be(reason, name);
    }

    [Fact]
    public void Range_edges_count_as_within()
    {
        // $31,579 × 95 = 3,000,005 just reaches 30L; $57,895 × 95 = 5,500,025 starts just above 55L.
        SalaryRule.Apply(Usd(20_000, 31_579), Prefs(), UsdInr, 8).Keep.Should().BeTrue();
        SalaryRule.Apply(Usd(57_895, 60_000), Prefs(), UsdInr, 8).Keep.Should().BeFalse();
        SalaryRule.Apply(new Salary(5_500_000, 6_000_000, "INR", "year", SalaryKinds.Stated), Prefs(), 1m, 8).Keep.Should().BeTrue();
    }
}
