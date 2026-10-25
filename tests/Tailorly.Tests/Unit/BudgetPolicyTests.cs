using FluentAssertions;
using Tailorly.App.Ai;
using Tailorly.Core.Budgets;
using Tailorly.Core.Ports;
using Tailorly.Core.Time;

namespace Tailorly.Tests.Unit;

public sealed class BudgetPolicyTests
{
    [Theory]
    [InlineData(600_000, 0, 600_000, 0, false)]
    [InlineData(600_000, 212_340, 387_660, 35, false)]
    [InlineData(1_400_000, 1_399_999, 1, 99, false)]
    [InlineData(1_400_000, 1_400_000, 0, 100, true)]
    [InlineData(1_400_000, 1_450_000, 0, 100, true)] // bounded overshoot from concurrent calls
    [InlineData(0, 0, 0, 100, true)]
    public void Snapshot_reports_remaining_percent_and_exhaustion(long limit, long used, long remaining, int percent, bool exhausted)
    {
        var s = new BudgetSnapshot(BudgetBuckets.Documents, limit, used);
        s.Remaining.Should().Be(remaining);
        s.PercentUsed.Should().Be(percent);
        s.IsExhausted.Should().Be(exhausted);
    }

    [Fact]
    public void Warnings_start_at_80_percent_and_escalate_at_100()
    {
        var warnings = BudgetPolicy.Warnings([
            new BudgetSnapshot(BudgetBuckets.Discovery, 600_000, 479_999), // 79 %
            new BudgetSnapshot(BudgetBuckets.Documents, 1_400_000, 1_130_000), // 80 %
        ]);
        warnings.Should().Equal("documents_80_percent");

        BudgetPolicy.Warnings([
            new BudgetSnapshot(BudgetBuckets.Discovery, 600_000, 600_000),
            new BudgetSnapshot(BudgetBuckets.Documents, 1_400_000, 0),
        ]).Should().Equal("discovery_100_percent");
    }

    [Fact]
    public void Only_discovery_and_documents_are_charged_to_users()
    {
        BudgetBuckets.IsPerUser(BudgetBuckets.Discovery).Should().BeTrue();
        BudgetBuckets.IsPerUser(BudgetBuckets.Documents).Should().BeTrue();
        BudgetBuckets.IsPerUser(BudgetBuckets.Platform).Should().BeFalse();
    }

    [Fact]
    public void Limits_come_from_configuration_per_bucket()
    {
        var o = new BudgetOptions { DiscoveryTokens = 10, DocumentsTokens = 20 };
        o.LimitFor(BudgetBuckets.Discovery).Should().Be(10);
        o.LimitFor(BudgetBuckets.Documents).Should().Be(20);
        var act = () => o.LimitFor(BudgetBuckets.Platform);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(1000, 200, 0, 0, 1200)]
    [InlineData(1000, 200, 300, 1000, 1600)] // cache reads count at 10 %
    [InlineData(0, 0, 0, 5, 1)]              // rounded up: never free
    public void Counted_tokens_follow_PRD_6_3(int input, int output, int cacheCreate, int cacheRead, long expected) =>
        new ModelUsage(input, output, cacheCreate, cacheRead).CountedTokens.Should().Be(expected);

    [Fact]
    public void Reset_date_is_the_first_of_next_month_in_IST_formatted_for_the_api()
    {
        var india = TimeZones.RequireIana(TimeZones.India);
        // 31 Oct 2026 20:00 UTC is already 1 Nov in India.
        var resets = PeriodKey.ResetsOn(new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero), india);
        BudgetPolicy.FormatResetsOn(resets).Should().Be("2026-12-01");
    }
}
