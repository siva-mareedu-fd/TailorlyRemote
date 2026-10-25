using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>
/// The all-endpoints isolation test (SDD §14.1, ApiDesign §5). Routes are discovered from the running app, so every
/// new route is covered automatically:
/// <list type="bullet">
/// <item>a route with an id is called by user B with user A's id and must return 404;</item>
/// <item>a GET without an id is called by user B and must not contain any of user A's data;</item>
/// <item>anything else must be listed below as public or as an action on the caller's own data;</item>
/// <item>every non-public route must refuse anonymous callers with 401.</item>
/// </list>
/// A route that fits none of these fails <see cref="Every_api_route_is_classified"/> until it is classified here.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed partial class IsolationTests(PostgresAppFixture fx)
{
    private TailorlyAppFactory F => fx.Factory;

    /// <summary>Routes that work without signing in.</summary>
    private static readonly HashSet<string> Public =
    [
        "GET /api/v1/meta", "GET /api/v1/auth/csrf", "POST /api/v1/auth/register", "POST /api/v1/auth/verify-email",
        "POST /api/v1/auth/login", "POST /api/v1/auth/forgot-password", "POST /api/v1/auth/reset-password",
        "POST /api/v1/account/confirm-deletion",
    ];

    /// <summary>Routes without an id that only act on the caller's own data (not called blindly: they create or send mail).</summary>
    private static readonly Dictionary<string, string> OwnActions = new()
    {
        ["POST /api/v1/resumes"] = "uploads a résumé for the caller",
        ["POST /api/v1/me/confirmed-skills"] = "adds a skill for the caller",
        ["PUT /api/v1/me"] = "updates the caller's profile",
        ["POST /api/v1/me/consent"] = "records the caller's consent",
        ["POST /api/v1/auth/logout"] = "ends the caller's session",
        ["POST /api/v1/auth/resend-verification"] = "mails the caller",
        ["POST /api/v1/auth/change-password"] = "changes the caller's password",
        ["DELETE /api/v1/account"] = "mails the caller a deletion link",
    };

    private sealed record Owner(TestUser User, Guid ResumeId, Guid SkillId, string FileName, string Skill);

    /// <summary>How to find user A's resource for a route with an id, by route prefix (longest prefix wins).</summary>
    private static readonly (string Prefix, Func<Owner, Guid> Id)[] Resolvers =
    [
        ("/api/v1/resumes/{", o => o.ResumeId),
        ("/api/v1/me/confirmed-skills/{", o => o.SkillId),
    ];

    /// <param name="Multipart">The endpoint only accepts multipart/form-data (otherwise routing answers 415 first).</param>
    private sealed record Route(string Method, string Pattern, bool Multipart)
    {
        public override string ToString() => $"{Method} {Pattern}";
        public bool HasParameters => Pattern.Contains('{', StringComparison.Ordinal);
    }

    private List<Route> ApiRoutes() => F.Services.GetRequiredService<EndpointDataSource>().Endpoints
        .OfType<RouteEndpoint>()
        .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
        .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            .Select(m => new Route(m, e.RoutePattern.RawText!.TrimEnd('/'),
                e.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IAcceptsMetadata>()?.ContentTypes
                    .Any(c => c.StartsWith("multipart/", StringComparison.Ordinal)) == true)))
        .Distinct()
        .OrderBy(r => r.Pattern, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal)
        .ToList();

    private static Task<HttpResponseMessage> CallAsync(ApiClient api, Route route, string path)
    {
        if (route.Multipart)
            return api.SendAsync(new HttpRequestMessage(new HttpMethod(route.Method), path)
                { Content = new MultipartFormDataContent { { new StringContent("x"), "file", "x.docx" } } });
        return api.SendJsonAsync(new HttpMethod(route.Method), path, route.Method is "POST" or "PUT" or "PATCH" ? new { } : null);
    }

    private static string Normalize(Route r) => $"{r.Method} {Constraint().Replace(r.Pattern, "")}";

    private static Func<Owner, Guid>? ResolverFor(Route r) =>
        Resolvers.Where(x => r.Pattern.StartsWith(x.Prefix, StringComparison.Ordinal))
            .OrderByDescending(x => x.Prefix.Length).Select(x => x.Id).FirstOrDefault();

    [Fact]
    public void Every_api_route_is_classified()
    {
        var routes = ApiRoutes();
        routes.Should().NotBeEmpty();
        var unclassified = routes.Where(r =>
        {
            var key = Normalize(r);
            if (Public.Contains(key) || OwnActions.ContainsKey(key)) return false;
            if (r.HasParameters) return ResolverFor(r) is null;
            return r.Method != "GET";
        }).Select(r => r.ToString()).ToList();

        unclassified.Should().BeEmpty("every route must be public, an own-data action, a GET, or have an id resolver in IsolationTests");
        (Public.Concat(OwnActions.Keys)).Should().OnlyContain(k => routes.Any(r => Normalize(r) == k),
            "the lists must not name routes that no longer exist");
    }

    [Fact]
    public async Task User_B_cannot_read_or_change_user_A_resources_through_any_route()
    {
        var owner = await CreateOwnerAsync();
        var intruder = await TestUsers.CreateVerifiedAsync(F, "isolation-b", "Isolation Intruder B");
        var markers = new[]
        {
            owner.User.Id.ToString(), owner.User.Email, owner.ResumeId.ToString(), owner.SkillId.ToString(), owner.FileName,
            owner.Skill, "Isolation Owner A",
        };

        var failures = new List<string>();
        foreach (var route in ApiRoutes().Where(r => !Public.Contains(Normalize(r))))
        {
            if (route.HasParameters)
            {
                var id = ResolverFor(route)!(owner);
                var path = Regex.Replace(route.Pattern, @"\{[^}]+\}", id.ToString());
                var res = await CallAsync(intruder.Api, route, path);
                if (res.StatusCode != HttpStatusCode.NotFound)
                    failures.Add($"{route} → {(int)res.StatusCode} (expected 404)");
            }
            else if (route.Method == "GET")
            {
                var res = await intruder.Api.GetAsync(route.Pattern);
                var body = await ApiClient.TextAsync(res);
                if (!res.IsSuccessStatusCode) failures.Add($"{route} → {(int)res.StatusCode} for the intruder's own data");
                foreach (var m in markers.Where(m => body.Contains(m, StringComparison.OrdinalIgnoreCase)))
                    failures.Add($"{route} leaked user A's data ({m})");
            }
        }
        failures.Should().BeEmpty();

        // Nothing B attempted changed A's data.
        (await owner.User.Api.GetAsync($"/api/v1/resumes/{owner.ResumeId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiClient.TextAsync(await owner.User.Api.GetAsync("/api/v1/me/confirmed-skills"))).Should().Contain(owner.Skill);
        (await ApiClient.JsonAsync(await owner.User.Api.GetAsync($"/api/v1/resumes/{owner.ResumeId}")))
            .GetProperty("isActive").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Anonymous_callers_get_401_on_every_non_public_route()
    {
        var anon = await F.CreateApiClientAsync(); // has a CSRF token, so 401 is about authentication
        var failures = new List<string>();
        foreach (var route in ApiRoutes().Where(r => !Public.Contains(Normalize(r))))
        {
            var path = Regex.Replace(route.Pattern, @"\{[^}]+\}", Guid.NewGuid().ToString());
            var res = await CallAsync(anon, route, path);
            if (res.StatusCode != HttpStatusCode.Unauthorized) failures.Add($"{route} → {(int)res.StatusCode}");
        }
        failures.Should().BeEmpty();
    }

    private async Task<Owner> CreateOwnerAsync()
    {
        var a = await TestUsers.CreateVerifiedAsync(F, "isolation-a", "Isolation Owner A");
        var fileName = $"owner-a-{Guid.NewGuid():N}.docx";
        var resumeId = await TestUsers.UploadParsedResumeAsync(F, a, fileName: fileName);
        var skill = "ZebraLang" + Guid.NewGuid().ToString("N")[..6];
        var skillRes = await a.Api.PostAsync("/api/v1/me/confirmed-skills", new { skill });
        var skillId = (await ApiClient.JsonAsync(skillRes)).GetProperty("id").GetGuid();
        return new Owner(a, resumeId, skillId, fileName, skill);
    }

    [GeneratedRegex(@":[a-z]+(?=\})")]
    private static partial Regex Constraint();
}
