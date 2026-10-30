using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Features.Digests;
using Tailorly.App.Features.Jobs;
using Tailorly.App.Infrastructure.Data;
using Tailorly.App.Infrastructure.Fx;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Unit.Matching;

public sealed class DigestEmailTemplateTests
{
    private static JobSummaryDto Job(string title, string source = "remoteok", string sourceName = "Remote OK", int? fit = 9,
        string eligibility = "worldwide", string applyUrl = "https://remoteok.com/remote-jobs/123") => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        CompanyName = "Acme <Cloud>",
        LocationText = "Remote",
        Eligibility = eligibility,
        Salary = new SalaryDto(60_000, 90_000, "USD", "year", "year", "stated", new ApproxInrDto(5_700_000, 8_550_000)),
        SalaryLabel = "stated",
        DescriptionCompleteness = "full",
        ApplyUrl = applyUrl,
        Source = new SourceInfoDto(source, sourceName, "Source: " + sourceName, "https://remoteok.com", "https://remoteok.com/remote-jobs/123"),
        AlsoOn = [],
        Score = fit is { } f ? new JobScoreDto(f, ["Strong .NET match", "Seniority fits", "Third reason"], [], [], "high") : null,
    };

    private static DigestEmailModel Model(IReadOnlyList<JobSummaryDto> jobs, int? total = null, string? degraded = null) => new(
        "user@example.test", "Ravi", new DateOnly(2026, 10, 30), jobs, total ?? jobs.Count, degraded, "2026-11-01", "18:00 Asia/Kolkata",
        "https://tailorly.test/shortlist", id => $"https://tailorly.test/shortlist?job={id}", "https://tailorly.test/preferences",
        "https://tailorly.test/api/v1/email/unsubscribe?token=abc");

    [Fact]
    public void Every_item_has_a_followed_attribution_link_apply_link_and_open_in_app()
    {
        var job = Job("Senior Backend Engineer (.NET)");
        var mail = DigestEmailTemplate.Build(Model([job]));

        mail.HtmlBody.Should().Contain("""<a href="https://remoteok.com/remote-jobs/123" style="color:#6a675c">via Remote OK</a>""");
        mail.HtmlBody.Should().NotContain("nofollow");
        mail.HtmlBody.Should().Contain("Apply on remoteok.com").And.Contain($"shortlist?job={job.Id}").And.Contain("Open in app");
        mail.TextBody.Should().Contain("via Remote OK: https://remoteok.com/remote-jobs/123").And.Contain("Apply: https://remoteok.com/remote-jobs/123");
        mail.Subject.Should().Be("1 new remote match for you · Tailorly Remote");
    }

    [Fact]
    public void Item_shows_fit_eligibility_salary_and_a_two_line_reason()
    {
        var mail = DigestEmailTemplate.Build(Model([Job("Platform Engineer", eligibility: "unclear")]));
        var html = System.Net.WebUtility.HtmlDecode(mail.HtmlBody);
        html.Should().Contain("Fit 9/10").And.Contain("Eligibility unclear").And.Contain("$60k–$90k (stated) ≈ ₹57L–₹85.5L");
        html.Should().Contain("Strong .NET match · Seniority fits").And.NotContain("Third reason");
    }

    [Fact]
    public void Job_text_is_html_encoded()
    {
        var mail = DigestEmailTemplate.Build(Model([Job("<script>alert(1)</script>")]));
        mail.HtmlBody.Should().NotContain("<script>").And.Contain("&lt;script&gt;").And.Contain("Acme &lt;Cloud&gt;");
    }

    [Fact]
    public void Non_http_apply_links_are_not_rendered()
    {
        var mail = DigestEmailTemplate.Build(Model([Job("Engineer", applyUrl: "javascript:alert(1)")]));
        mail.HtmlBody.Should().NotContain("javascript:");
    }

    [Fact]
    public void Carries_the_one_click_unsubscribe_url_and_a_visible_link()
    {
        var mail = DigestEmailTemplate.Build(Model([Job("Engineer")]));
        mail.ListUnsubscribeUrl.Should().Be("https://tailorly.test/api/v1/email/unsubscribe?token=abc");
        mail.HtmlBody.Should().Contain("Turn off the daily digest");
    }

    [Fact]
    public void Degraded_digest_explains_the_missing_scores()
    {
        var mail = DigestEmailTemplate.Build(Model([Job("Engineer", fit: null)], degraded: "budget"));
        mail.HtmlBody.Should().Contain("Discovery budget for this month is used up until 2026-11-01").And.Contain("Not scored");
    }

    [Fact]
    public void Empty_digest_says_so()
    {
        var mail = DigestEmailTemplate.Build(Model([]));
        mail.Subject.Should().StartWith("No new matches today");
    }

    [Theory]
    [InlineData(3_000_000, "INR", "₹30L")]
    [InlineData(12_500_000, "INR", "₹1.25Cr")]
    [InlineData(85_000, "USD", "$85k")]
    [InlineData(55_500, "EUR", "€55.5k")]
    [InlineData(900, "SGD", "SGD 900")]
    public void Formats_money_compactly(long amount, string currency, string expected) =>
        DigestEmailTemplate.Money(amount, currency).Should().Be(expected);

    [Fact]
    public void Recipient_is_the_confirmed_notification_email_else_the_sign_in_email()
    {
        var p = new UserProfile { UserId = Guid.NewGuid(), DisplayName = "R", NotificationEmail = "alerts@example.test" };
        DigestEmailJob.Recipient(p, "me@example.test").Should().Be("me@example.test");
        p.NotificationEmailVerifiedAt = DateTimeOffset.UtcNow;
        DigestEmailJob.Recipient(p, "me@example.test").Should().Be("alerts@example.test");
        var same = new UserProfile { UserId = Guid.NewGuid(), DisplayName = "R", NotificationEmail = "ME@example.test" };
        DigestEmailJob.Recipient(same, "me@example.test").Should().Be("ME@example.test");
    }
}

/// <summary>Time-zone boundaries of the 4-hour retry window (the dispatcher's own SQL is tested in integration tests).</summary>
public sealed class DigestWindowTests
{
    [Fact]
    public void India_window_ends_four_hours_after_schedule_time()
    {
        PerUserRunJob.WindowEnd(new DateOnly(2026, 10, 30), new TimeOnly(18, 0), "Asia/Kolkata", 4)
            .Should().Be(new DateTimeOffset(2026, 10, 30, 16, 30, 0, TimeSpan.Zero)); // 22:00 IST
    }

    [Fact]
    public void A_late_evening_window_crosses_midnight()
    {
        PerUserRunJob.WindowEnd(new DateOnly(2026, 10, 30), new TimeOnly(22, 30), "Asia/Kolkata", 4)
            .Should().Be(new DateTimeOffset(2026, 10, 30, 21, 0, 0, TimeSpan.Zero)); // 02:30 IST on the 31st
    }

    [Fact]
    public void Dst_zone_uses_the_offset_of_that_day()
    {
        // New York: EDT (-4) until 1 Nov 2026, EST (-5) after.
        PerUserRunJob.WindowEnd(new DateOnly(2026, 10, 30), new TimeOnly(18, 0), "America/New_York", 4)
            .Should().Be(new DateTimeOffset(2026, 10, 31, 2, 0, 0, TimeSpan.Zero));
        PerUserRunJob.WindowEnd(new DateOnly(2026, 11, 2), new TimeOnly(18, 0), "America/New_York", 4)
            .Should().Be(new DateTimeOffset(2026, 11, 3, 3, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_schedule_time_skipped_by_dst_starts_at_the_next_valid_time()
    {
        // 8 Mar 2026: New York clocks jump from 02:00 to 03:00, so 02:30 does not exist.
        PerUserRunJob.WindowEnd(new DateOnly(2026, 3, 8), new TimeOnly(2, 30), "America/New_York", 4)
            .Should().Be(new DateTimeOffset(2026, 3, 8, 11, 0, 0, TimeSpan.Zero)); // 03:00 EDT + 4 h
    }

    [Fact]
    public void Unknown_zone_gives_no_window() =>
        PerUserRunJob.WindowEnd(new DateOnly(2026, 10, 30), new TimeOnly(18, 0), "Mars/Olympus", 4).Should().BeNull();
}

public sealed class FxProviderTests
{
    [Fact]
    public void Parses_the_recorded_frankfurter_v2_response()
    {
        var rates = FrankfurterFxRateProvider.Parse(JsonDocument.Parse(Fixtures.Read("fx/frankfurter_v2_rates_usd.json")));
        rates["USD"].Should().Be(1m);
        rates["INR"].Should().BeInRange(50m, 200m);
        rates.Should().ContainKeys("EUR", "GBP", "SGD");
    }

    [Theory]
    [InlineData("{\"status\":422,\"message\":\"invalid currency: XXX\"}")]
    [InlineData("[]")]
    [InlineData("[{\"base\":\"EUR\",\"quote\":\"INR\",\"rate\":100}]")]
    public void Rejects_responses_without_usable_rates(string json)
    {
        var act = () => FrankfurterFxRateProvider.Parse(JsonDocument.Parse(json));
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Static_rates_answer_inverse_and_cross_pairs()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fx:StaticRates:USD_INR"] = "95", ["Fx:StaticRates:EUR_USD"] = "1.1",
        }).Build();
        var fx = new StaticFxRateProvider(config);
        var ct = TestContext.Current.CancellationToken;
        (await fx.GetRateAsync("USD", "INR", ct)).Should().Be(95m);
        (await fx.GetRateAsync("INR", "USD", ct)).Should().BeApproximately(1m / 95m, 0.000001m);
        (await fx.GetRateAsync("EUR", "INR", ct)).Should().Be(104.5m);
        (await fx.GetRateAsync("GBP", "INR", ct)).Should().BeNull();
    }
}

public sealed class MatchingTaskTests
{
    private static EnrichmentOutput Enrichment(string summary = "A backend role.", int? min = 3, int? max = 6) => new(
        ["C#"], [], [], "senior", min, max, "full_time", "remote", "worldwide", "Remote, worldwide", [], null, "employee", null, summary);

    [Fact]
    public void Enrichment_validator_checks_summary_length_and_ranges()
    {
        JobEnrichmentTask.Validate(Enrichment()).Should().BeEmpty();
        JobEnrichmentTask.Validate(Enrichment(string.Join(' ', Enumerable.Repeat("word", 61)))).Should().ContainSingle();
        JobEnrichmentTask.Validate(Enrichment(min: 8, max: 5)).Should().ContainSingle();
    }

    [Theory]
    [InlineData("Open to candidates in APAC time zones", true)]
    [InlineData("  \"open to CANDIDATES in apac   time zones\" ", true)]
    [InlineData("Open to candidates worldwide", false)]
    [InlineData(null, false)]
    public void Evidence_is_kept_only_when_it_quotes_the_posting(string? evidence, bool kept)
    {
        const string text = "Senior Engineer\nRemote\nWe are a remote company. Open to candidates in APAC time zones.";
        (JobEnrichmentTask.QuotedEvidence(evidence, text) is not null).Should().Be(kept);
    }

    [Fact]
    public void Fit_score_validator_limits_reasons()
    {
        var ok = new FitScoreOutput(8, ["Short reason"], [], [], "high");
        JobFitScoreTask.Validate(ok).Should().BeEmpty();
        JobFitScoreTask.Validate(ok with { Reasons = [] }).Should().NotBeEmpty();
        JobFitScoreTask.Validate(ok with { Reasons = ["a", "b", "c", "d"] }).Should().NotBeEmpty();
        JobFitScoreTask.Validate(ok with { Reasons = [string.Join(' ', Enumerable.Repeat("w", 21))] }).Should().NotBeEmpty();
    }

    [Fact]
    public void Confidence_is_never_high_for_a_limited_description()
    {
        JobFitScoreTask.CapConfidence("high", "limited").Should().Be("medium");
        JobFitScoreTask.CapConfidence("high", "full").Should().Be("high");
        JobFitScoreTask.CapConfidence("low", "limited").Should().Be("low");
    }

    [Fact]
    public void Scoring_input_uses_enrichment_facts_and_never_the_raw_description()
    {
        var facts = new FitJobFacts("Backend Engineer", "Acme", "Remote", "remote", "full_time", "worldwide", "full", Enrichment());
        var input = JobFitScoreTask.BuildInput("<candidate_data>\nsummary\n</candidate_data>", facts);
        input.Data.Should().ContainSingle(b => b.Tag == "job_data");
        input.Data[0].Content.Should().Contain("Required skills: C#").And.Contain("Job summary: A backend role.");
        input.CacheableContext.Should().Contain("candidate_data");
    }

    [Fact]
    public void Candidate_summary_leaves_out_contact_details_and_employers()
    {
        var profile = new ParsedProfile(new ParsedContact("Ravi K", "ravi@example.test", "+91 99999", "Hyderabad", ["https://linkedin.com/in/x"]),
            "Backend engineer.", [new ParsedSkill("C#", "language")],
            [new ParsedRole("Secret Employer Ltd", "Senior Engineer", "2021-01", null, ["Did things"])], [], [], 6, ["payments"]);
        var summary = JobFitScoreTask.CandidateSummary(profile, ["Kubernetes"], new FitPreferences(["Backend engineer"], null, "Up to 4h overlap with US East"));
        summary.Should().Contain("Senior Engineer").And.Contain("C#").And.Contain("Kubernetes").And.Contain("US East");
        summary.Should().NotContain("ravi@example.test").And.NotContain("+91").And.NotContain("Secret Employer").And.NotContain("Ravi K");
    }
}

public sealed class DigestOptionsTests
{
    [Fact]
    public void Configured_retry_delays_replace_the_defaults_instead_of_being_appended()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Digest:RetryDelayMinutes:0"] = "10", ["Digest:RetryDelayMinutes:1"] = "30", ["Digest:RetryDelayMinutes:2"] = "60",
        }).Build();
        config.GetSection("Digest").Get<DigestOptions>()!.RetryDelays.Should().Equal(10, 30, 60);
        new DigestOptions().RetryDelays.Should().Equal(10, 30, 60);
    }
}
