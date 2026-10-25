using System.Net;
using System.Text.Json;
using FluentAssertions;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>
/// Journey A with the real Anthropic provider (M1 exit criterion). Skipped unless <c>TAILORLY_LIVE_ANTHROPIC_KEY</c>
/// is set; it sends one synthetic résumé (no real person's data) and spends a few thousand Documents tokens.
/// With <c>TAILORLY_RECORD_FIXTURES=1</c> the parsed reply is saved as the fake provider's recorded fixture.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LiveAnthropicTests(PostgresAppFixture fx)
{
    [Fact]
    public async Task Resume_parse_works_end_to_end_with_the_real_provider()
    {
        var key = Environment.GetEnvironmentVariable("TAILORLY_LIVE_ANTHROPIC_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Assert.Skip("Set TAILORLY_LIVE_ANTHROPIC_KEY to run against the real Anthropic API.");
            return;
        }

        await using var f = new TailorlyAppFactory(fx.ConnectionString, new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "Anthropic",
            ["Anthropic:ApiKey"] = key,
        });
        var u = await TestUsers.CreateVerifiedAsync(f, "live");
        var id = await TestUsers.UploadParsedResumeAsync(f, u);

        var resume = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/resumes/{id}"));
        resume.GetProperty("parseStatus").GetString().Should().Be("ready", $"errorCode: {resume.GetProperty("errorCode")}");
        resume.GetProperty("isActive").GetBoolean().Should().BeTrue();

        var profileRes = await u.Api.GetAsync($"/api/v1/resumes/{id}/profile");
        profileRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var parsed = (await ApiClient.JsonAsync(profileRes)).GetProperty("parsed");
        parsed.GetProperty("experience").GetArrayLength().Should().Be(2);
        parsed.GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("name").GetString()).Should().Contain("PostgreSQL");
        parsed.GetProperty("experience")[0].GetProperty("company").GetString().Should().Contain("Example Corp");

        var usage = await Db.StringsAsync(fx.ConnectionString,
            "SELECT model || ' ' || success || ' ' || counted_tokens FROM ai_usage WHERE user_id = @u", ("u", u.Id));
        usage.Should().NotBeEmpty().And.Contain(r => r.Contains(" true ", StringComparison.Ordinal));

        if (Environment.GetEnvironmentVariable("TAILORLY_RECORD_FIXTURES") == "1")
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "Tailorly.App", "Infrastructure", "Ai", "Fixtures", "resume_parse.json"));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                TestContext.Current.CancellationToken);
        }
    }
}
