using FluentAssertions;
using Tailorly.Core.Jobs;

namespace Tailorly.Tests.Unit.Sources;

public sealed class DedupeKeyTests
{
    [Theory]
    [InlineData("Acme Cloud, Inc.", "acme cloud")]
    [InlineData("The Acme Company Pvt Ltd", "acme")]
    [InlineData("Wise & Co GmbH", "wise and")]
    [InlineData("GitLab", "gitlab")]
    public void Normalises_company_names(string name, string expected) => NameNormalizer.Company(name).Should().Be(expected);

    [Theory]
    [InlineData("Sr. Back-End Engineer (Remote)", "senior backend engineer")]
    [InlineData("Senior Backend Engineer - 100% Remote", "senior backend engineer")]
    [InlineData("Jr Front End Dev", "junior frontend developer")]
    public void Normalises_titles(string title, string expected) => NameNormalizer.Title(title).Should().Be(expected);

    [Fact]
    public void The_same_remote_role_gets_one_key_across_sources()
    {
        // Himalayas: country list → remote-region:india; Greenhouse: "Remote, India" text; Remote OK: no location.
        var himalayas = DedupeKey.Compute("Acme Cloud", "Senior Backend Engineer", "remote-region:india", WorkModes.Remote);
        var greenhouse = DedupeKey.Compute("Acme Cloud Inc.", "Sr. Backend Engineer (Remote)", "remote-region:india", WorkModes.Remote);
        var remoteOk = DedupeKey.Compute("ACME CLOUD", "Senior Back End Engineer", "unknown", WorkModes.Remote);
        greenhouse.Should().Be(himalayas);
        remoteOk.Should().Be(himalayas);
        himalayas.Should().MatchRegex("^[0-9a-f]{40}$");
    }

    [Fact]
    public void Different_cities_or_titles_get_different_keys()
    {
        var a = DedupeKey.Compute("Acme", "Backend Engineer", "city:bengaluru", WorkModes.Onsite);
        DedupeKey.Compute("Acme", "Backend Engineer", "city:pune", WorkModes.Onsite).Should().NotBe(a);
        DedupeKey.Compute("Acme", "Frontend Engineer", "city:bengaluru", WorkModes.Onsite).Should().NotBe(a);
        DedupeKey.Compute("Acme", "Backend Engineer", "unknown", WorkModes.Remote).Should().NotBe(a);
    }
}

public sealed class DedupeRankingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private static DedupeCandidate C(int n, bool board, bool requireApplyUrl, bool full, int minutes = 0) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{n:D12}"), board, requireApplyUrl, full, T0.AddMinutes(minutes));

    [Fact]
    public void An_ats_record_beats_a_himalayas_copy_even_when_the_ats_text_is_limited()
    {
        var himalayas = C(1, board: false, requireApplyUrl: true, full: true);
        var greenhouse = C(2, board: true, requireApplyUrl: false, full: false, minutes: 10);
        DedupeRanking.Winner([himalayas, greenhouse]).Should().Be(greenhouse);
        DedupeRanking.Winner([greenhouse, himalayas]).Should().Be(greenhouse);
    }

    [Fact]
    public void Among_feeds_the_full_description_wins_then_the_oldest()
    {
        var limitedOld = C(1, false, true, full: false);
        var fullNew = C(2, false, true, full: true, minutes: 30);
        DedupeRanking.Winner([limitedOld, fullNew]).Should().Be(fullNew);

        var fullOld = C(3, false, true, full: true, minutes: 5);
        DedupeRanking.Winner([fullNew, fullOld]).Should().Be(fullOld, "ties go to the oldest so the winner does not flap");
    }

    [Fact]
    public void Without_a_require_apply_url_source_completeness_comes_before_the_board()
    {
        var feedFull = C(1, board: false, requireApplyUrl: false, full: true);
        var boardLimited = C(2, board: true, requireApplyUrl: false, full: false);
        DedupeRanking.Winner([boardLimited, feedFull]).Should().Be(feedFull);
        DedupeRanking.Winner([feedFull, C(3, true, false, true, 50)]).Id.Should().Be(C(3, true, false, true).Id);
    }
}

public sealed class EligibilityRulesTests
{
    [Fact]
    public void Structured_country_lists_decide_first()
    {
        EligibilityRules.Classify([], "US only", null, null).Eligibility.Should().Be(Eligibility.Worldwide);
        EligibilityRules.Classify(["India", "United States"], null, null, null).Eligibility.Should().Be(Eligibility.IndiaEligible);
        var restricted = EligibilityRules.Classify(["United States", "Canada"], null, null, null);
        restricted.Eligibility.Should().Be(Eligibility.Restricted);
        restricted.Evidence.Should().Contain("United States");
    }

    [Theory]
    [InlineData("Bengaluru, India", null, Eligibility.IndiaEligible)]
    [InlineData("Remote - APAC", null, Eligibility.IndiaEligible)]
    [InlineData("Remote, France", null, Eligibility.Restricted)]
    [InlineData("San Francisco, CA", null, Eligibility.Restricted)]
    [InlineData("Remote (US)", null, Eligibility.Restricted)]
    [InlineData("Anywhere in the World", null, Eligibility.Worldwide)]
    [InlineData("Remote", "You must be authorized to work in the United States.", Eligibility.Restricted)]
    [InlineData("Remote", "Candidates must reside in the EU for tax reasons.", Eligibility.Restricted)]
    [InlineData("Remote", "This role is US-only.", Eligibility.Restricted)]
    [InlineData("Remote", "Join us! We are a fully distributed team and hire from anywhere.", Eligibility.Worldwide)]
    [InlineData("Remote", "We have an engineering hub in Pune.", Eligibility.IndiaEligible)]
    [InlineData("Remote", "Join us to build great things.", Eligibility.Unclear)]
    [InlineData(null, null, Eligibility.Unclear)]
    public void Phrase_rules(string? location, string? description, string expected) =>
        EligibilityRules.Classify(null, location, "Backend Engineer", description).Eligibility.Should().Be(expected);

    [Fact]
    public void A_hard_restriction_beats_worldwide_wording()
    {
        var hint = EligibilityRules.Classify(null, "Remote", "Engineer",
            "Work from anywhere you like! Applicants must be located in the United States.");
        hint.Eligibility.Should().Be(Eligibility.Restricted);
        hint.Evidence.Should().Contain("located in the United States");
        hint.Evidence!.Length.Should().BeLessThanOrEqualTo(120);
    }
}

public sealed class AtsUrlTests
{
    [Theory]
    [InlineData("https://boards.greenhouse.io/gitlab", "greenhouse", "gitlab", null)]
    [InlineData("https://job-boards.greenhouse.io/GitLab/jobs/8860302002?gh_src=x", "greenhouse", "gitlab", "8860302002")]
    [InlineData("https://boards.greenhouse.io/embed/job_app?for=stripe&token=123", "greenhouse", "stripe", "123")]
    [InlineData("https://jobs.lever.co/palantir/6ed76ce8-4156-4b60-b120-403538bd66cd/apply", "lever", "palantir",
        "6ed76ce8-4156-4b60-b120-403538bd66cd")]
    [InlineData("https://jobs.ashbyhq.com/Ashby/7458d4e9-da2e-47bd-98cb-adfda43d42b2", "ashby", "ashby",
        "7458d4e9-da2e-47bd-98cb-adfda43d42b2")]
    [InlineData("http://jobs.lever.co/acme", "lever", "acme", null)]
    public void Parses_allowlisted_board_urls(string url, string platform, string token, string? jobId)
    {
        AtsUrl.TryParse(url, out var b).Should().BeTrue();
        b.Should().Be(new AtsBoardRef(platform, token, jobId));
    }

    [Theory]
    [InlineData("https://evil.example/boards.greenhouse.io/acme")]
    [InlineData("https://boards.greenhouse.io.evil.example/acme")]
    [InlineData("https://user:pass@boards.greenhouse.io/acme")]
    [InlineData("https://boards.greenhouse.io:8443/acme")]
    [InlineData("https://job-boards.eu.greenhouse.io/acme")]
    [InlineData("https://boards-api.greenhouse.io/v1/boards/acme/jobs")]
    [InlineData("https://boards.greenhouse.io/%2e%2e")]
    [InlineData("https://boards.greenhouse.io/a")]
    [InlineData("https://boards.greenhouse.io/acme%2Fother")]
    [InlineData("https://jobs.lever.co/")]
    [InlineData("ftp://jobs.lever.co/acme")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://localhost:8080/acme")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Rejects_everything_else(string url) => AtsUrl.TryParse(url, out _).Should().BeFalse();

    [Theory]
    [InlineData("acme", true)]
    [InlineData("acme-cloud_2", true)]
    [InlineData("a", false)]
    [InlineData("Acme", false)]
    [InlineData("acme/../x", false)]
    [InlineData("acme.com", false)]
    [InlineData("acme%20", false)]
    public void Token_rule_matches_the_database_check(string token, bool ok)
    {
        AtsUrl.IsValidToken(token).Should().Be(ok);
        var act = () => AtsUrl.RequireToken(token);
        if (ok) act.Should().NotThrow();
        else act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Finds_board_links_inside_html()
    {
        var found = AtsUrl.FindAll("<p>Apply <a href=\"https://job-boards.greenhouse.io/acme/jobs/1?x=1&amp;y=2\">here</a>" +
                                   " or at https://jobs.lever.co/acme/abc. Not https://boards.greenhouse.io.evil.com/x</p>");
        found.Should().BeEquivalentTo([new AtsBoardRef("greenhouse", "acme", "1"), new AtsBoardRef("lever", "acme", "abc")]);
    }
}
