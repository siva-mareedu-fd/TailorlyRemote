using System.Text.Json;
using FluentAssertions;
using Tailorly.App.Features.Sources.Adapters;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Unit.Sources;

/// <summary>Each adapter against real responses captured on 2026-10-25 (Fixtures/Sources).</summary>
public sealed class AdapterFixtureTests
{
    private static JsonElement Json(string fixture) => JsonDocument.Parse(Fixtures.Read(fixture)).RootElement.Clone();

    [Fact]
    public void Himalayas_india_search_maps_structured_restrictions_and_keeps_the_application_link()
    {
        var page = HimalayasSource.Parse(Json("himalayas/search_country_in_excl_ww_p1.json"), page: 1);
        page.Jobs.Should().HaveCount(6);
        page.SkippedRows.Should().Be(0);
        page.NextCursor.Should().Be("2");
        foreach (var j in page.Jobs)
        {
            j.SourceKey.Should().Be("himalayas");
            j.ExternalId.Should().StartWith("https://himalayas.app/companies/");
            j.ApplyUrl.Should().StartWith("https://himalayas.app/").And.Contain("utm_source=api", "the API's applicationLink is used unchanged");
            j.CountryRestrictions.Should().Contain("India");
            j.WorkplaceType.Should().Be("remote");
            j.PostedAt.Should().BeAfter(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
            j.ExpiresAt.Should().BeAfter(j.PostedAt!.Value);
            EligibilityRules.Classify(j.CountryRestrictions, j.LocationText, j.Title, null).Eligibility.Should().Be(Eligibility.IndiaEligible);
        }
    }

    [Fact]
    public void Himalayas_worldwide_search_has_no_restrictions()
    {
        var page = HimalayasSource.Parse(Json("himalayas/search_worldwide_p1.json"), page: 1);
        page.Jobs.Should().NotBeEmpty().And.OnlyContain(j => j.CountryRestrictions!.Count == 0 && j.LocationText == "Worldwide");
    }

    [Fact]
    public void Himalayas_salaries_keep_their_published_period()
    {
        var jobs = new[] { "himalayas/search_country_in_p1.json", "himalayas/search_worldwide_p1.json" }
            .SelectMany(f => HimalayasSource.Parse(Json(f), 1).Jobs).ToList();
        jobs.Select(j => j.SalaryPeriod).Distinct().Should().Contain("year").And.Contain("hour");
        var hourly = jobs.First(j => j.SalaryPeriod == "hour" && j.SalaryMin is not null);
        var s = SalaryParser.FromStructured(hourly.SalaryMin, hourly.SalaryMax, hourly.SalaryCurrency, hourly.SalaryPeriod, false);
        s.Min.Should().Be((long)(hourly.SalaryMin!.Value * 2080));
    }

    [Fact]
    public void Himalayas_pins_featured_jobs_at_the_top_of_page_one()
    {
        var jobs = HimalayasSource.Parse(Json("himalayas/search_country_in_p1.json"), 1).Jobs;
        jobs[0].PostedAt.Should().BeBefore(jobs[^1].PostedAt!.Value, "the first jobs are featured, not newest; the runner reads the last");
    }

    [Fact]
    public void Himalayas_empty_page_ends_the_stream_and_an_error_body_is_bad_payload()
    {
        var empty = HimalayasSource.Parse(JsonDocument.Parse("""{"offset":40,"limit":20,"totalCount":40,"jobs":[]}""").RootElement, 3);
        empty.Jobs.Should().BeEmpty();
        empty.NextCursor.Should().BeNull();

        // The real 400 body for a bad filter: {"ok":false,"errors":"Invalid country"}
        var act = () => HimalayasSource.Parse(Json("himalayas/search_invalid_country.json"), 1);
        act.Should().Throw<SourceException>().Which.Failure.Should().Be(SourceFailure.BadPayload);
    }

    [Fact]
    public void RemoteOk_skips_the_legal_notice_strips_the_requester_note_and_flags_teasers()
    {
        var page = RemoteOkSource.Parse(Json("remoteok/api.json"));
        page.NextCursor.Should().BeNull();
        page.Jobs.Should().HaveCount(11);
        page.Jobs.Should().OnlyContain(j => j.ApplyUrl.StartsWith("https://remoteOK.com/", StringComparison.OrdinalIgnoreCase));
        page.Jobs.Should().OnlyContain(j => !j.DescriptionHtml!.Contains("Please mention the word"), "the note embeds the requester's IP");
        page.Jobs.Should().Contain(j => j.DescriptionIsPartial).And.Contain(j => !j.DescriptionIsPartial);
        var paid = page.Jobs.First(j => j.SalaryMin is not null);
        (paid.SalaryCurrency, paid.SalaryPeriod).Should().Be(("USD", "year"));
        page.Jobs.Where(j => j.SalaryMin is null).Should().OnlyContain(j => j.SalaryCurrency == null, "0 means no salary");
        page.Jobs.Should().OnlyContain(j => j.PostedAt != null && j.SourceMeta.ContainsKey("tags"));
    }

    [Fact]
    public void Greenhouse_list_without_content_and_detail_with_content()
    {
        var list = GreenhouseSource.ParseList(Json("greenhouse/gitlab_jobs.json"), "gitlab", withContent: false);
        list.CompanyName.Should().Be("GitLab");
        list.HasDescriptions.Should().BeFalse();
        list.Jobs.Should().HaveCount(5).And.OnlyContain(j => j.ExternalId.StartsWith("gitlab:") && j.DescriptionHtml == null);

        var detail = GreenhouseSource.Map(Json("greenhouse/gitlab_job_detail.json"), "gitlab")!;
        detail.ExternalId.Should().Be(list.Jobs[0].ExternalId);
        detail.ApplyUrl.Should().StartWith("https://job-boards.greenhouse.io/gitlab/jobs/");
        var text = HtmlText.ToPlainText(detail.DescriptionHtml);
        text.Should().Contain("GitLab").And.NotContain("&lt;").And.NotContain("<p>");
        text.Length.Should().BeGreaterThan(JobNormalizer.MinFullDescriptionChars);
    }

    [Fact]
    public void Lever_and_ashby_lists_carry_descriptions()
    {
        var lever = LeverSource.ParseList(Json("lever/palantir_postings.json"), "palantir");
        lever.HasDescriptions.Should().BeTrue();
        lever.Jobs.Should().HaveCount(2).And.OnlyContain(j => j.ExternalId.StartsWith("palantir:") && j.PostedAt!.Value.Year >= 2020);
        lever.Jobs[0].ApplyUrl.Should().EndWith("/apply");

        var ashby = AshbySource.ParseList(Json("ashby/ashby_job_board.json"), "ashby");
        ashby.Jobs.Should().HaveCount(2);
        var first = ashby.Jobs[0];
        first.ExternalId.Should().StartWith("ashby:");
        first.WorkplaceType.Should().Be("Remote");
        (first.SalaryCurrency, first.SalaryPeriod).Should().Be(("EUR", "year"));
        first.SalaryMin.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("{\"unexpected\": true}")]
    [InlineData("[1, 2, 3]")]
    public void A_changed_schema_is_reported_as_bad_payload(string body)
    {
        var json = JsonDocument.Parse(body).RootElement;
        var feed = () => HimalayasSource.Parse(json, 1);
        feed.Should().Throw<SourceException>().Which.Failure.Should().Be(SourceFailure.BadPayload);
        var board = () => GreenhouseSource.ParseList(json, "acme", false);
        board.Should().Throw<SourceException>().Which.Failure.Should().Be(SourceFailure.BadPayload);
    }

    [Fact]
    public void Rows_missing_required_fields_are_skipped_not_fatal()
    {
        var json = JsonDocument.Parse("""[{"legal":"…"}, {"id":"1","position":"Engineer"}, {"id":"2","position":"Dev","company":"A","url":"https://remoteok.com/x"}]""")
            .RootElement;
        var page = RemoteOkSource.Parse(json);
        page.Jobs.Should().ContainSingle(j => j.ExternalId == "2");
        page.SkippedRows.Should().Be(1);
    }
}

public sealed class RemoteOkEncodingTests
{
    [Theory]
    [InlineData("software â\u0080\u0094 not", "software — not")]
    [InlineData("ð\u009f\u008e¯ Targets", "\U0001F3AF Targets")]
    [InlineData("Café au lait", "Café au lait")]
    [InlineData("plain ascii", "plain ascii")]
    [InlineData("already — fine", "already — fine")]
    public void Repairs_double_encoded_utf8_only_when_it_round_trips(string input, string expected) =>
        RemoteOkSource.RepairUtf8(input).Should().Be(expected);
}
