using System.Net;
using System.Text.Json;
using FluentAssertions;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class HealthAndErrorsTests(PostgresAppFixture fx)
{
    private readonly HttpClient _client = fx.Factory.CreateHttpsClient();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Healthz_returns_200()
    {
        var res = await _client.GetAsync("/healthz", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readyz_returns_200_when_database_is_reachable()
    {
        var res = await _client.GetAsync("/readyz", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readyz_returns_503_when_database_is_unreachable()
    {
        await using var down = new TailorlyAppFactory("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1");
        var res = await down.CreateHttpsClient().GetAsync("/readyz", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // Liveness does not depend on the database.
        (await down.CreateHttpsClient().GetAsync("/healthz", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Api_returns_503_problem_when_database_is_unreachable()
    {
        await using var down = new TailorlyAppFactory("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1");
        var res = await down.CreateHttpsClient().GetAsync("/api/v1/meta", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        doc.RootElement.GetProperty("code").GetString().Should().Be("SERVICE_UNAVAILABLE");
        doc.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Unknown_api_route_returns_problem_details_with_code_and_trace_id()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/does-not-exist");
        req.Headers.Add("X-Correlation-Id", "test-corr-123");
        var res = await _client.SendAsync(req, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        res.Headers.GetValues("X-Correlation-Id").Single().Should().Be("test-corr-123");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        var root = doc.RootElement;
        root.GetProperty("status").GetInt32().Should().Be(404);
        root.GetProperty("code").GetString().Should().Be("NOT_FOUND");
        root.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("type").GetString().Should().EndWith("/errors/not-found");
        root.GetProperty("traceId").GetString().Should().Be("test-corr-123");
    }

    [Fact]
    public async Task Unsafe_correlation_id_is_replaced()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        req.Headers.TryAddWithoutValidation("X-Correlation-Id", "bad id\" injected");
        var res = await _client.SendAsync(req, Ct);
        res.Headers.GetValues("X-Correlation-Id").Single().Should().MatchRegex("^[a-f0-9]{32}$");
    }

    [Fact]
    public async Task Admin_job_dashboard_rejects_anonymous_users()
    {
        var res = await _client.GetAsync("/admin/jobs", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
