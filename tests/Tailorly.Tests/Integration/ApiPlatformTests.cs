using System.Net;
using System.Text.Json;
using FluentAssertions;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class ApiPlatformTests(PostgresAppFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Meta_returns_configuration_for_the_spa()
    {
        var res = await fx.Factory.CreateHttpsClient().GetAsync("/api/v1/meta", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        var root = doc.RootElement;
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("consentVersion").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("requireInviteCode").GetBoolean().Should().BeFalse();
        root.GetProperty("tones").EnumerateArray().Select(t => t.GetString()).Should().Equal("concise", "confident");
        root.GetProperty("limits").GetProperty("resumeMaxMb").GetInt32().Should().Be(5);
        root.GetProperty("limits").GetProperty("watchlistMax").GetInt32().Should().Be(25);
    }

    [Fact]
    public async Task Api_responses_carry_security_headers()
    {
        var res = await fx.Factory.CreateHttpsClient().GetAsync("/api/v1/meta", Ct);

        res.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("default-src 'self'")
            .And.Contain("frame-ancestors 'none'").And.NotContain("unsafe-inline");
        res.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        res.Headers.GetValues("Referrer-Policy").Single().Should().Be("strict-origin-when-cross-origin");
        res.Headers.GetValues("Permissions-Policy").Should().ContainSingle();
        res.Headers.CacheControl!.NoStore.Should().BeTrue();
        res.Headers.Contains("Server").Should().BeFalse();
    }

    [Fact]
    public async Task Unsafe_request_without_csrf_header_is_rejected()
    {
        var res = await fx.Factory.CreateHttpsClient().PostAsync("/api/v1/meta", content: null, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        doc.RootElement.GetProperty("code").GetString().Should().Be("CSRF_INVALID");
    }

    [Fact]
    public async Task Csrf_endpoint_issues_readable_token_that_passes_validation()
    {
        var client = fx.Factory.CreateHttpsClient();
        var issue = await client.GetAsync("/api/v1/auth/csrf", Ct);
        issue.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var cookies = issue.Headers.GetValues("Set-Cookie").ToList();
        var xsrf = cookies.Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        xsrf.Should().NotContain("httponly", "the SPA must be able to read it");
        cookies.Should().Contain(c => c.StartsWith("__Host-tailorly-af=", StringComparison.Ordinal) &&
                                      c.Contains("secure", StringComparison.OrdinalIgnoreCase) &&
                                      c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        var token = Uri.UnescapeDataString(xsrf.Split(';')[0]["XSRF-TOKEN=".Length..]);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/meta");
        req.Headers.Add("X-XSRF-TOKEN", token);
        var res = await client.SendAsync(req, Ct);

        // CSRF passes; the route itself only allows GET, and a wrong method is reported like an unknown route.
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        res.Content.Headers.Allow.Should().BeEmpty();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(404);
        doc.RootElement.GetProperty("code").GetString().Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Rate_limit_returns_429_problem_with_retry_after()
    {
        await using var factory = new TailorlyAppFactory(fx.ConnectionString, new Dictionary<string, string?>
        {
            ["RateLimits:api-default:PermitLimit"] = "2",
            ["RateLimits:api-default:WindowSeconds"] = "60",
        });
        var client = factory.CreateHttpsClient();

        (await client.GetAsync("/api/v1/meta", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/meta", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var res = await client.GetAsync("/api/v1/meta", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        res.Headers.Contains("Retry-After").Should().BeTrue();
        res.Headers.GetValues("RateLimit-Limit").Single().Should().Be("2");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        doc.RootElement.GetProperty("code").GetString().Should().Be("RATE_LIMITED");
    }
}
