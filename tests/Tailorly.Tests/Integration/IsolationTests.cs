using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Features.Resumes;
using Tailorly.App.Infrastructure.Jobs;
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
        ["PUT /api/v1/preferences"] = "replaces the caller's preferences",
        ["POST /api/v1/watched-companies"] = "adds a board to the caller's watch list",
    };

    /// <summary>Admin routes: a signed-in user without the Admin role gets 403 on every one, whatever the id.</summary>
    private const string AdminPrefix = "/api/v1/admin/";

    private sealed record Owner(TestUser User, Guid ResumeId, Guid SkillId, string FileName, string Skill, Guid WatchedLinkId,
        string Role, string BoardToken);

    /// <summary>How to find user A's resource for a route with an id, by route prefix (longest prefix wins).</summary>
    private static readonly (string Prefix, Func<Owner, Guid> Id)[] Resolvers =
    [
        ("/api/v1/resumes/{", o => o.ResumeId),
        ("/api/v1/me/confirmed-skills/{", o => o.SkillId),
        ("/api/v1/watched-companies/{", o => o.WatchedLinkId),
    ];

    /// <param name="Multipart">The endpoint only accepts multipart/form-data (otherwise routing answers 415 first).</param>
    private sealed record Route(string Method, string Pattern, bool Multipart)
    {
        public override string ToString() => $"{Method} {Pattern}";
        public bool HasParameters => Pattern.Contains('{', StringComparison.Ordinal);
        public bool IsAdmin => Pattern.StartsWith(AdminPrefix, StringComparison.Ordinal);
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
            if (Public.Contains(key) || OwnActions.ContainsKey(key) || r.IsAdmin) return false;
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
            owner.Skill, "Isolation Owner A", owner.Role, owner.BoardToken, owner.WatchedLinkId.ToString(),
        };

        var failures = new List<string>();
        foreach (var route in ApiRoutes().Where(r => !Public.Contains(Normalize(r))))
        {
            if (route.IsAdmin)
            {
                var path = Regex.Replace(route.Pattern, @"\{[^}]+\}", owner.WatchedLinkId.ToString());
                var res = await CallAsync(intruder.Api, route, path);
                if (res.StatusCode != HttpStatusCode.Forbidden) failures.Add($"{route} → {(int)res.StatusCode} (expected 403 for a non-admin)");
            }
            else if (route.HasParameters)
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

    /// <summary>Routes outside <c>/api/v1</c>: none serves user data except the admin-only job dashboard.</summary>
    private static readonly Dictionary<string, string> NonApi = new()
    {
        ["/healthz"] = "liveness, no data",
        ["/readyz"] = "readiness, no data",
        [$"{HangfireSetup.DashboardPath}/{{**path}}"] = "Hangfire dashboard, Admin policy plus dashboard filter",
    };

    [Fact]
    public void Every_non_api_route_is_known()
    {
        var routes = F.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!)
            .Where(p => !p.StartsWith("/api/v1", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        routes.Should().BeEquivalentTo(NonApi.Keys, "a new route outside /api/v1 is not covered by the isolation tests; classify it here");
    }

    [Fact]
    public async Task Job_dashboard_is_forbidden_to_signed_in_non_admins()
    {
        var user = await TestUsers.CreateVerifiedAsync(F, "isolation-dash");
        foreach (var path in new[] { HangfireSetup.DashboardPath, $"{HangfireSetup.DashboardPath}/jobs/enqueued" })
        {
            var res = await user.Api.GetAsync(path);
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
        }
    }

    [Fact]
    public async Task Usage_counts_only_the_callers_tokens()
    {
        var owner = await CreateOwnerAsync(); // its parse charged the Documents bucket
        var intruder = await TestUsers.CreateVerifiedAsync(F, "isolation-b", "Isolation Intruder B");

        static async Task<long> DocumentsUsed(TestUser u) => (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/usage")))
            .GetProperty("buckets").EnumerateArray().Single(b => b.GetProperty("bucket").GetString() == "documents")
            .GetProperty("used").GetInt64();

        (await DocumentsUsed(owner.User)).Should().BePositive();
        (await DocumentsUsed(intruder)).Should().Be(0);
    }

    [Fact]
    public async Task Own_data_actions_never_resolve_to_another_users_rows()
    {
        var owner = await CreateOwnerAsync();
        var intruder = await TestUsers.CreateVerifiedAsync(F, "isolation-b", "Isolation Intruder B");

        // The skill de-duplication lookup must not find A's row.
        var skill = await intruder.Api.PostAsync("/api/v1/me/confirmed-skills", new { skill = owner.Skill.ToUpperInvariant() });
        skill.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ApiClient.JsonAsync(skill)).GetProperty("id").GetGuid().Should().NotBe(owner.SkillId);

        // A's parse is in flight; B's upload must not be "de-duplicated" onto A's résumé.
        var aPending = await owner.User.Api.UploadAsync("/api/v1/resumes", owner.FileName, DocxBuilder.SampleResume());
        aPending.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var aPendingId = (await ApiClient.JsonAsync(aPending)).GetProperty("id").GetGuid();
        var bUpload = await intruder.Api.UploadAsync("/api/v1/resumes", "b.docx", DocxBuilder.SampleResume());
        bUpload.StatusCode.Should().Be(HttpStatusCode.Accepted, await ApiClient.TextAsync(bUpload));
        var bBody = await ApiClient.TextAsync(bUpload);
        bBody.Should().NotContain(aPendingId.ToString()).And.NotContain(owner.FileName);

        await F.Jobs.RunAllAsync();
        (await ApiClient.JsonAsync(await owner.User.Api.GetAsync($"/api/v1/resumes/{aPendingId}")))
            .GetProperty("parseStatus").GetString().Should().Be("ready");
    }

    [Fact]
    public async Task A_parse_job_run_for_another_user_cannot_touch_the_resume()
    {
        var owner = await TestUsers.CreateVerifiedAsync(F, "isolation-a", "Isolation Owner A");
        var intruder = await TestUsers.CreateVerifiedAsync(F, "isolation-b", "Isolation Intruder B");
        var res = await owner.Api.UploadAsync("/api/v1/resumes", "a.docx", DocxBuilder.SampleResume());
        var resumeId = (await ApiClient.JsonAsync(res)).GetProperty("id").GetGuid();

        // Same job, A's résumé id, B's user id (what a tampered job argument would look like).
        await using (var scope = F.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ParseResumeJob>()
                .RunAsync(resumeId, intruder.Id, null, TestContext.Current.CancellationToken);

        var a = await ApiClient.JsonAsync(await owner.Api.GetAsync($"/api/v1/resumes/{resumeId}"));
        a.GetProperty("parseStatus").GetString().Should().Be("queued", "the job must not see A's résumé when acting as B");
        (await owner.Api.GetAsync($"/api/v1/resumes/{resumeId}/profile")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await F.Jobs.RunAllAsync(); // A's own job still parses it
        (await ApiClient.JsonAsync(await owner.Api.GetAsync($"/api/v1/resumes/{resumeId}")))
            .GetProperty("parseStatus").GetString().Should().Be("ready");
    }

    private async Task<Owner> CreateOwnerAsync()
    {
        var a = await TestUsers.CreateVerifiedAsync(F, "isolation-a", "Isolation Owner A");
        var fileName = $"owner-a-{Guid.NewGuid():N}.docx";
        var resumeId = await TestUsers.UploadParsedResumeAsync(F, a, fileName: fileName);
        var skill = "ZebraLang" + Guid.NewGuid().ToString("N")[..6];
        var skillRes = await a.Api.PostAsync("/api/v1/me/confirmed-skills", new { skill });
        var skillId = (await ApiClient.JsonAsync(skillRes)).GetProperty("id").GetGuid();

        var role = "ZebraRole" + Guid.NewGuid().ToString("N")[..6];
        var prefs = await ApiClient.JsonAsync(await a.Api.GetAsync("/api/v1/preferences"));
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(prefs.GetRawText())!;
        body["targetRoles"] = new[] { role };
        (await a.Api.PutAsync("/api/v1/preferences", body)).StatusCode.Should().Be(HttpStatusCode.OK);

        var token = "zebra-" + Guid.NewGuid().ToString("N")[..10];
        F.Http.Always($"{SourcesTestData.GreenhouseApi}{token}/jobs", 200, SourcesTestData.GreenhouseList("Zebra Owner Co", (1, "Engineer", "Remote")));
        var watched = await a.Api.PostAsync("/api/v1/watched-companies", new { boardUrl = $"https://boards.greenhouse.io/{token}" });
        watched.StatusCode.Should().Be(HttpStatusCode.Created, await ApiClient.TextAsync(watched));
        var linkId = (await ApiClient.JsonAsync(watched)).GetProperty("id").GetGuid();
        F.Jobs.Clear(); // the eager board poll is not part of this test
        return new Owner(a, resumeId, skillId, fileName, skill, linkId, role, token);
    }

    [GeneratedRegex(@":[a-z]+(?=\})")]
    private static partial Regex Constraint();
}
