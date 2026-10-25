using FluentAssertions;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;

namespace Tailorly.Tests.Unit.Sources;

public sealed class HtmlTextTests
{
    [Fact]
    public void Strips_tags_scripts_and_styles_and_keeps_paragraphs()
    {
        var text = HtmlText.ToPlainText("<h2>About</h2><p>We&nbsp;build &amp; ship.</p><script>alert(1)</script>" +
                                        "<style>p{}</style><ul><li>C#</li><li>SQL</li></ul><!-- hidden -->Done");
        text.Should().Be("About\nWe build & ship.\n• C#\n• SQL\nDone");
        text.Should().NotContain("alert").And.NotContain("hidden").And.NotContain("<");
    }

    [Fact]
    public void Decodes_entity_encoded_html_once_like_greenhouse_sends_it()
    {
        HtmlText.ToPlainText("&lt;p&gt;Remote &amp;amp; async&lt;/p&gt;&lt;br/&gt;Team").Should().Be("Remote & async\nTeam");
    }

    [Fact]
    public void Strips_tags_that_were_escaped_inside_real_html()
    {
        // Remote OK listing 1137542: real tags plus escaped ones in the same field.
        HtmlText.ToPlainText("<p>Hello</p><p> join us.&lt;/p&gt;&lt;h3&gt;&lt;/h3&gt;</p>").Should().Be("Hello\njoin us.");
        HtmlText.ToPlainText("<p>Use List&lt;string&gt; and a &lt; b</p>").Should().Be("Use List<string> and a < b");
    }

    [Fact]
    public void Empty_or_whitespace_is_empty() => HtmlText.ToPlainText("  <p> </p> ").Should().BeEmpty();

    [Fact]
    public void Caps_very_long_input() => HtmlText.ToPlainText(new string('a', 400_000)).Length.Should().Be(HtmlText.MaxLength);
}

public sealed class SalaryParserTests
{
    [Theory]
    [InlineData(55000, 70000, "USD", "annual", 55000, 70000, "year")]
    [InlineData(40, 80, "USD", "hourly", 83200, 166400, "hour")]
    [InlineData(2900, 4300, "EUR", "monthly", 34800, 51600, "month")]
    [InlineData(100, 100, "usd", "weekly", 5200, 5200, "week")]
    public void Annualises_structured_salaries(int min, int max, string cur, string period, long eMin, long eMax, string ePeriod)
    {
        var s = SalaryParser.FromStructured(min, max, cur, period, estimate: false);
        s.Should().Be(new Salary(eMin, eMax, cur.ToUpperInvariant(), ePeriod, SalaryKinds.Stated));
    }

    [Fact]
    public void Missing_currency_or_amounts_is_unknown()
    {
        SalaryParser.FromStructured(50_000, null, null, "annual", false).Should().Be(Salary.None);
        SalaryParser.FromStructured(null, null, "INR", "monthly", false).Should().Be(Salary.None);
        SalaryParser.FromStructured(0, -5, "USD", null, false).Should().Be(Salary.None);
    }

    [Fact]
    public void Estimated_salaries_say_so_and_swapped_ranges_are_fixed()
    {
        var s = SalaryParser.FromStructured(90_000, 60_000, "USD", null, estimate: true);
        s.Should().Be(new Salary(60_000, 90_000, "USD", "year", SalaryKinds.Estimated));
    }

    [Theory]
    [InlineData("The base salary range is $120,000 - $150,000 per year.", 120_000, 150_000, "USD")]
    [InlineData("Compensation: USD 55k–70k annually", 55_000, 70_000, "USD")]
    [InlineData("Pay: €4,000 per month", 48_000, 48_000, "EUR")]
    [InlineData("Hourly rate $40/hr to $60/hr", 83_200, 124_800, "USD")]
    [InlineData("CTC 18-25 LPA for the right candidate", 1_800_000, 2_500_000, "INR")]
    public void Finds_salaries_in_text_near_pay_words(string text, long min, long max, string currency)
    {
        var s = SalaryParser.FromText(text);
        s.Min.Should().Be(min);
        s.Max.Should().Be(max);
        s.Currency.Should().Be(currency);
        s.Kind.Should().Be(SalaryKinds.Stated);
    }

    [Theory]
    [InlineData("We raised $120M from top investors and have 300 employees.")]
    [InlineData("Our database handles $5,000 transactions a second.")]
    [InlineData("No numbers here at all")]
    public void Ignores_money_that_is_not_a_salary(string text) => SalaryParser.FromText(text).Should().Be(Salary.None);
}

public sealed class JobFieldsTests
{
    [Theory]
    [InlineData("Full Time", EmploymentTypes.FullTime)]
    [InlineData("FullTime", EmploymentTypes.FullTime)]
    [InlineData("Full-time", EmploymentTypes.FullTime)]
    [InlineData("Contractor", EmploymentTypes.Contract)]
    [InlineData("Temporary", EmploymentTypes.Contract)]
    [InlineData("Part Time", EmploymentTypes.PartTime)]
    [InlineData("Intern", EmploymentTypes.Internship)]
    [InlineData("Volunteer", null)]
    [InlineData(null, null)]
    public void Maps_employment_types(string? raw, string? expected) => JobFields.EmploymentType(raw).Should().Be(expected);

    [Theory]
    [InlineData("remote", null, false, WorkModes.Remote)]
    [InlineData("OnSite", "Remote - Europe", false, WorkModes.Onsite)]
    [InlineData(null, "Hybrid - Bengaluru", false, WorkModes.Hybrid)]
    [InlineData(null, "Remote, India", false, WorkModes.Remote)]
    [InlineData(null, "Bengaluru, India", false, WorkModes.Onsite)]
    [InlineData(null, null, true, WorkModes.Remote)]
    [InlineData(null, null, false, null)]
    public void Maps_work_mode(string? type, string? location, bool remoteOnly, string? expected) =>
        JobFields.WorkMode(type, location, remoteOnly).Should().Be(expected);

    [Theory]
    [InlineData(WorkModes.Remote, new string[0], null, "remote-worldwide")]
    [InlineData(WorkModes.Remote, new[] { "India" }, null, "remote-region:india")]
    [InlineData(WorkModes.Remote, null, "Remote, France", "remote-region:france")]
    [InlineData(WorkModes.Remote, null, "Anywhere in the World", "remote-worldwide")]
    [InlineData(WorkModes.Remote, null, "Remote", "unknown")]
    [InlineData(WorkModes.Onsite, null, "Bengaluru, Karnataka, India", "city:bengaluru")]
    [InlineData(null, null, null, "unknown")]
    public void Classifies_location(string? workMode, string[]? countries, string? text, string expected) =>
        JobFields.LocationClass(workMode, countries, text).Should().Be(expected);
}

public sealed class JobNormalizerTests
{
    private static RawJob Raw(string? html = null, bool partial = false) => new()
    {
        SourceKey = "remoteok", ExternalId = "1", Title = "  Sr. Backend Engineer ", CompanyName = "Acme &amp; Co",
        ApplyUrl = "https://remoteok.com/remote-jobs/1", SourceUrl = "https://remoteok.com/remote-jobs/1",
        DescriptionHtml = html, DescriptionIsPartial = partial,
    };

    [Fact]
    public void Cleans_fields_and_marks_short_or_partial_text_as_limited()
    {
        var full = JobNormalizer.Normalize(Raw("<p>" + new string('x', 500) + "</p>"), new SourceTraits(RemoteOnly: true));
        full.Title.Should().Be("Sr. Backend Engineer");
        full.CompanyName.Should().Be("Acme & Co");
        full.WorkMode.Should().Be(WorkModes.Remote);
        full.DescriptionFull.Should().BeTrue();
        full.ContentHash.Should().HaveLength(64);

        JobNormalizer.Normalize(Raw("<p>short</p>"), new SourceTraits(true)).DescriptionFull.Should().BeFalse();
        JobNormalizer.Normalize(Raw("<p>" + new string('x', 500) + "...</p>", partial: true), new SourceTraits(true))
            .DescriptionFull.Should().BeFalse();
    }

    [Fact]
    public void Falls_back_to_salary_in_the_text()
    {
        var p = JobNormalizer.Normalize(Raw("<p>Salary: $100,000 - $120,000 per year</p>"), new SourceTraits(true));
        p.Salary.Should().Be(new Salary(100_000, 120_000, "USD", "year", SalaryKinds.Stated));
    }
}
