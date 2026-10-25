using FluentAssertions;
using Tailorly.Core.Time;

namespace Tailorly.Tests.Unit;

public sealed class TimeTests
{
    [Fact]
    public void Self_test_resolves_Asia_Kolkata_at_plus_5_30()
    {
        var tz = TimeZones.RequireIana(TimeZones.India);
        tz.GetUtcOffset(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)).Should().Be(TimeSpan.FromMinutes(330));
    }

    [Fact]
    public void Self_test_fails_fast_for_an_unknown_zone()
    {
        var act = () => TimeZones.RequireIana("Mars/Olympus_Mons");
        act.Should().Throw<InvalidOperationException>().WithMessage("*tzdata*");
    }

    [Theory]
    [InlineData("Asia/Kolkata", true)]
    [InlineData("America/New_York", true)]
    [InlineData("Not/AZone", false)]
    [InlineData("", false)]
    public void IsValid_checks_iana_names(string id, bool expected) => TimeZones.IsValid(id).Should().Be(expected);

    [Theory]
    [InlineData("Asia/Calcutta", "Asia/Kolkata")]
    [InlineData(" asia/calcutta ", "Asia/Kolkata")]
    [InlineData("Europe/Kiev", "Europe/Kyiv")]
    [InlineData("Asia/Kolkata", "Asia/Kolkata")]
    [InlineData("America/New_York", "America/New_York")]
    public void Legacy_aliases_that_browsers_report_are_canonicalised(string given, string expected)
    {
        TimeZones.Canonicalize(given).Should().Be(expected);
        TimeZones.IsValid(given).Should().BeTrue();
    }

    [Fact]
    public void Period_key_rolls_over_at_midnight_IST_not_UTC()
    {
        var india = TimeZones.RequireIana(TimeZones.India);
        // 2026-10-31 18:29:59Z = 23:59:59 IST → still October
        PeriodKey.For(new DateTimeOffset(2026, 10, 31, 18, 29, 59, TimeSpan.Zero), india).Should().Be("2026-10");
        // 2026-10-31 18:30:00Z = 00:00 IST on 1 Nov → November
        PeriodKey.For(new DateTimeOffset(2026, 10, 31, 18, 30, 0, TimeSpan.Zero), india).Should().Be("2026-11");
        PeriodKey.ResetsOn(new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero), india).Should().Be(new DateOnly(2027, 1, 1));
    }
}
