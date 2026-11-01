using FluentAssertions;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.MatchingTestData;

namespace Tailorly.Tests.Integration;

/// <summary>
/// M6 source-compliance checklist (PRD F14, §7.1a, Appendix D): every job the API returns carries its source's name,
/// attribution text and link as configured in <c>job_sources</c>, and its own listing URL; the per-source flags are those
/// of the seed; Himalayas cannot be enabled until its terms are marked reviewed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ComplianceTests(PostgresAppFixture fx) : IAsyncLifetime
{
    private TailorlyAppFactory F => fx.Factory;
    private string Cs => fx.ConnectionString;

    public ValueTask InitializeAsync()
    {
        F.Ai.ClearScript();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        F.Ai.ClearScript();
        await SourcesTestData.ResetAsync(F, Cs);
    }

    [Fact]
    public async Task Every_source_s_jobs_carry_its_attribution_and_listing_link()
    {
        var sources = (await Db.StringsAsync(Cs, """
            SELECT concat_ws('|', key, display_name, requires_attribution, coalesce(attribution_text, ''), coalesce(attribution_url, ''))
            FROM job_sources WHERE kind <> 'user' ORDER BY key
            """)).Select(r => r.Split('|')).ToList();
        sources.Select(s => s[0]).Should().BeEquivalentTo(["ashby", "greenhouse", "himalayas", "jobicy", "lever", "remoteok", "wwr"]);

        // Runs only consider enabled sources (a disabled source's data is never shown), so enable each one here.
        foreach (var s in sources) await SourcesTestData.EnableAsync(Cs, s[0]);
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role, "compliance");
        var jobs = new Dictionary<string, Guid>();
        foreach (var s in sources)
            jobs[s[0]] = await InsertJobAsync(F, $"{role} Engineer at {s[0]}", company: $"Compliance {s[0]}", source: s[0]);
        (await RunNowAsync(F, u)).GetProperty("status").GetString().Should().Be("ready");

        foreach (var s in sources)
        {
            var (key, name, requires, text, url) = (s[0], s[1], s[2] == "true", s[3], s[4]);
            var detail = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/jobs/{jobs[key]}"));
            var source = detail.GetProperty("source");
            source.GetProperty("key").GetString().Should().Be(key);
            source.GetProperty("name").GetString().Should().Be(name, "\"via {Source}\" uses the source's display name");
            source.GetProperty("sourceUrl").GetString().Should().StartWith("https://", $"{key}: the original listing is linked");
            detail.GetProperty("applyUrl").GetString().Should().StartWith("https://");
            if (requires)
            {
                source.GetProperty("attributionText").GetString().Should().Be(text, $"{key} requires attribution");
                source.GetProperty("attributionUrl").GetString().Should().Be(url);
            }
        }
    }

    [Fact]
    public async Task Per_source_flags_match_the_reviewed_terms()
    {
        var flags = await Db.StringsAsync(Cs, """
            SELECT concat_ws('|', key, requires_attribution, require_source_apply_url, forbid_third_party_resolution, purge_on_expiry)
            FROM job_sources ORDER BY key
            """);
        flags.Should().Equal(
            "ashby|f|f|f|f",
            "byo|f|f|f|f",
            "greenhouse|f|f|f|f",
            "himalayas|t|t|t|t",
            "jobicy|t|t|f|f",
            "lever|f|f|f|f",
            "remoteok|t|t|f|f",
            "wwr|t|t|f|f");
    }

    [Fact]
    public async Task Himalayas_is_enabled_only_after_its_terms_are_marked_reviewed()
    {
        await SourcesTestData.ResetAsync(F, Cs);
        var admin = await TestUsers.CreateAdminAsync(F);
        (await ApiClient.CodeAsync(await admin.Api.PutAsync("/api/v1/admin/sources/himalayas", new { enabled = true })))
            .Should().Be("TERMS_NOT_REVIEWED");
        (await Db.ScalarAsync<bool>(Cs, "SELECT enabled FROM job_sources WHERE key = 'himalayas'")).Should().BeFalse();
        (await Db.StringsAsync(Cs, "SELECT key FROM job_sources WHERE enabled ORDER BY key")).Should().Equal("byo", "greenhouse", "remoteok");
    }

    [Fact]
    public async Task Jobs_from_a_disabled_source_are_never_shown()
    {
        await SourcesTestData.ResetAsync(F, Cs); // himalayas disabled
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role, "compliance");
        var hidden = await InsertJobAsync(F, $"{role} Engineer at himalayas", source: "himalayas");
        var shown = await InsertJobAsync(F, $"{role} Engineer at remoteok", source: "remoteok");
        await RunNowAsync(F, u);
        JobIds(await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/jobs"))).Should().Contain(shown).And.NotContain(hidden);
        (await u.Api.GetAsync($"/api/v1/jobs/{hidden}")).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }
}
