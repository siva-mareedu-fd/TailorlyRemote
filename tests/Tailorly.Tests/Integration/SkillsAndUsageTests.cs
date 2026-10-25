using System.Net;
using FluentAssertions;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class ConfirmedSkillsTests(PostgresAppFixture fx)
{
    private TailorlyAppFactory F => fx.Factory;

    [Fact]
    public async Task Confirmed_skills_are_idempotent_on_the_lower_cased_skill()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "skills");
        var created = await u.Api.PostAsync("/api/v1/me/confirmed-skills", new { skill = "  Kubernetes " });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var skill = await ApiClient.JsonAsync(created);
        skill.GetProperty("skill").GetString().Should().Be("Kubernetes");

        var again = await u.Api.PostAsync("/api/v1/me/confirmed-skills", new { skill = "kubernetes" });
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiClient.JsonAsync(again)).GetProperty("id").GetGuid().Should().Be(skill.GetProperty("id").GetGuid());

        await u.Api.PostAsync("/api/v1/me/confirmed-skills", new { skill = "Azure" });
        var list = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/me/confirmed-skills"));
        list.EnumerateArray().Select(s => s.GetProperty("skill").GetString()).Should().Equal("Azure", "Kubernetes");

        var id = skill.GetProperty("id").GetGuid();
        (await u.Api.DeleteAsync($"/api/v1/me/confirmed-skills/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await u.Api.DeleteAsync($"/api/v1/me/confirmed-skills/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await u.Api.PostAsync("/api/v1/me/confirmed-skills", new { skill = "" })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await u.Api.PostAsync("/api/v1/me/confirmed-skills", new { skill = new string('x', 101) })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class UsageTests(PostgresAppFixture fx)
{
    private TailorlyAppFactory F => fx.Factory;

    [Fact]
    public async Task Usage_shows_both_buckets_with_limits_and_no_warnings_for_a_new_user()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "usage");
        var usage = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/usage"));
        usage.GetProperty("periodKey").GetString().Should().MatchRegex(@"^\d{4}-\d{2}$");
        usage.GetProperty("resetsOn").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-01$");
        var buckets = usage.GetProperty("buckets").EnumerateArray().ToList();
        buckets.Select(b => b.GetProperty("bucket").GetString()).Should().Equal("discovery", "documents");
        buckets[0].GetProperty("limit").GetInt64().Should().Be(600_000);
        buckets[1].GetProperty("limit").GetInt64().Should().Be(1_400_000);
        buckets.Should().OnlyContain(b => b.GetProperty("used").GetInt64() == 0 && b.GetProperty("percentUsed").GetInt32() == 0);
        usage.GetProperty("warnings").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Usage_warns_at_80_percent_and_at_100_percent()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "warn");
        await Db.SetUsedAsync(fx.ConnectionString, u.Id, "documents", 1_130_000, 1_400_000);
        await Db.SetUsedAsync(fx.ConnectionString, u.Id, "discovery", 600_000, 600_000);

        var usage = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/usage"));
        var docs = usage.GetProperty("buckets").EnumerateArray().Single(b => b.GetProperty("bucket").GetString() == "documents");
        docs.GetProperty("percentUsed").GetInt32().Should().Be(80);
        docs.GetProperty("remaining").GetInt64().Should().Be(270_000);
        usage.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Equal("discovery_100_percent", "documents_80_percent");
    }
}
