using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Features.Digests;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Ports;

namespace Tailorly.Tests.Infrastructure;

/// <summary>Arranging jobs, preferences and runs for the M3 matching and digest tests.</summary>
public static class MatchingTestData
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A full description (≥ 400 characters) that mentions the fake résumé's skills.</summary>
    public const string Description =
        "We are a remote-first product company building payment infrastructure. You will design and build backend services " +
        "in C# and .NET, own PostgreSQL schemas and queries, and ship REST APIs used by thousands of merchants. You will " +
        "work closely with product and design, review code, mentor engineers and take part in a light on-call rotation. " +
        "We value clear writing, pragmatic engineering and kindness. Kubernetes experience is a plus. We hire from anywhere " +
        "in the world and work asynchronously across time zones.";

    /// <summary>Inserts one canonical listing into the shared jobs table and returns its id.</summary>
    public static async Task<Guid> InsertJobAsync(TailorlyAppFactory f, string title, string company = "Acme Cloud",
        string? location = "Remote", string? description = Description, string source = "greenhouse", string? workMode = "remote",
        long? salaryMin = null, long? salaryMax = null, string? currency = null, string salaryKind = "unknown",
        DateTimeOffset? createdAt = null, string? sourceMeta = null, DateTimeOffset? postedAt = null)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var n = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var job = new Job
        {
            SourceKey = source,
            ExternalId = $"test:{n}",
            DedupeKey = n,
            CompanyName = company,
            Title = title,
            LocationText = location,
            WorkMode = workMode,
            DescriptionText = description,
            DescriptionCompleteness = description is { Length: >= 400 } ? Completeness.Full : Completeness.Limited,
            ApplyUrl = $"https://boards.greenhouse.io/test/jobs/{n}",
            SourceUrl = source == "remoteok" ? $"https://remoteok.com/remote-jobs/{n}" : $"https://boards.greenhouse.io/test/jobs/{n}",
            SalaryMin = salaryMin,
            SalaryMax = salaryMax,
            SalaryCurrency = currency,
            SalaryPeriodOriginal = currency is null ? null : "year",
            SalaryKind = salaryKind,
            SourceMeta = sourceMeta,
            PostedAt = postedAt ?? now.AddHours(-1),
            FetchedAt = now,
            CreatedAt = createdAt ?? now,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(Ct);
        return job.Id;
    }

    /// <summary>Reads the user's preferences, applies <paramref name="change"/> and saves them.</summary>
    public static async Task SetPreferencesAsync(TestUser u, Action<Dictionary<string, object?>> change)
    {
        var current = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/preferences"));
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(current.GetRawText())!;
        change(body);
        var res = await u.Api.PutAsync("/api/v1/preferences", body);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(res));
    }

    /// <summary>A role word no other test uses, so only jobs seeded with it pass the user's prefilter.</summary>
    public static string NewRole() => "Zebra" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// A verified user with a parsed résumé and saved preferences targeting <paramref name="role"/>. The résumé's skills
    /// are unique to this user, so jobs other tests left in the shared table never match.
    /// </summary>
    public static async Task<TestUser> CreateMatcherAsync(TailorlyAppFactory f, string role, string tag = "matcher",
        Action<Dictionary<string, object?>>? prefs = null)
    {
        var u = await TestUsers.CreateVerifiedAsync(f, tag);
        Reply(f, Tailorly.App.Ai.AiTaskNames.ResumeParse, new
        {
            contact = new { name = "Matcher", email = (string?)null, phone = (string?)null, location = "Hyderabad", links = Array.Empty<string>() },
            summary = "Engineer.",
            skills = new[] { new { name = role + "lang", category = "language" } },
            experience = new[] { new { company = "Example Corp", title = role + " Engineer", start = "2020-01", end = (string?)null, bullets = new[] { "Built things." } } },
            education = Array.Empty<object>(),
            certifications = Array.Empty<object>(),
            totalYears = 6,
            domains = Array.Empty<string>(),
        });
        await TestUsers.UploadParsedResumeAsync(f, u);
        await SetPreferencesAsync(u, p =>
        {
            p["targetRoles"] = new[] { role };
            prefs?.Invoke(p);
        });
        return u;
    }

    /// <summary>"Run now", then runs the background work; returns the finished digest.</summary>
    public static async Task<JsonElement> RunNowAsync(TailorlyAppFactory f, TestUser u)
    {
        var res = await u.Api.PostAsync("/api/v1/runs");
        res.StatusCode.Should().Be(HttpStatusCode.Accepted, await ApiClient.TextAsync(res));
        var id = (await ApiClient.JsonAsync(res)).GetProperty("id").GetGuid();
        await f.Jobs.RunAllAsync();
        return await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/digests/{id}"));
    }

    /// <summary>One dispatcher tick as of <paramref name="now"/>, in its own scope (like one app instance).</summary>
    public static async Task<IReadOnlyList<Guid>> DispatchAsync(TailorlyAppFactory f, DateTimeOffset now)
    {
        await using var scope = f.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DigestDispatcherJob>().DispatchAsync(now, Ct);
    }

    public static IEnumerable<JsonElement> Items(JsonElement digest) => digest.GetProperty("items").EnumerateArray();

    public static IEnumerable<Guid> JobIds(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid());

    /// <summary>A transient provider failure for the next call of a task (as an overloaded API would give).</summary>
    public static void FailNext(TailorlyAppFactory f, string task, int times = 1)
    {
        for (var i = 0; i < times; i++)
            f.Ai.Enqueue(task, _ => throw new ModelProviderException("overloaded", transient: true, 529));
    }

    /// <summary>A reply for the next call of a task (an anonymous object serialised as the model's JSON).</summary>
    public static void Reply(TailorlyAppFactory f, string task, object reply) =>
        f.Ai.Enqueue(task, JsonSerializer.Serialize(reply));

    /// <summary>
    /// Replies chosen by the job title in the request (calls run in parallel, so order is not fixed): the first marker
    /// found in the prompt picks the reply, otherwise the recorded fixture answers. Queues one router per expected call.
    /// </summary>
    public static void ReplyByTitle(TailorlyAppFactory f, string task, int calls, params (string Marker, object Reply)[] replies)
    {
        for (var i = 0; i < calls; i++)
            f.Ai.Enqueue(task, r =>
            {
                var text = string.Join("\n", r.Messages.Select(m => m.Text));
                foreach (var (marker, reply) in replies)
                    if (text.Contains(marker, StringComparison.Ordinal))
                        return FakeReply(r, JsonSerializer.Serialize(reply));
                return FakeReply(r, Fixture(task));
            });
    }

    private static ModelResponse FakeReply(ModelRequest r, string text) => Tailorly.App.Infrastructure.Ai.FakeModelProvider.Reply(r, text);

    private static string Fixture(string task)
    {
        var asm = typeof(Tailorly.App.Infrastructure.Ai.FakeModelProvider).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith($".Fixtures.{task}.json", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        return new StreamReader(s).ReadToEnd();
    }

    public static object Enrichment(string eligibility = "worldwide", string? evidence = null, string? workMode = "remote",
        string? hiring = "employee", object? salary = null) => new
    {
        skills_required = new[] { "C#", ".NET", "PostgreSQL" },
        skills_preferred = new[] { "Kubernetes" },
        skill_aliases = Array.Empty<object>(),
        seniority = "senior",
        years_experience_min = 5,
        years_experience_max = (int?)null,
        employment_type = "full_time",
        work_mode = workMode,
        eligibility,
        eligibility_evidence = evidence,
        restricted_regions = Array.Empty<string>(),
        timezone_requirement = (string?)null,
        hiring_model_hint = hiring,
        salary_extracted = salary,
        summary = "Backend role building .NET services.",
    };

    public static object Fit(int score, string confidence = "high") => new
    {
        fit_score = score,
        reasons = new[] { $"Scored {score} for the test", "Second reason" },
        matched_skills = new[] { "C#" },
        missing_skills = Array.Empty<string>(),
        confidence,
    };
}

/// <summary>A clock fixed at one instant (for services built by hand in tests).</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
