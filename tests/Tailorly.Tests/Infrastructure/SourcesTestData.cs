using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Features.Sources;
using Tailorly.App.Infrastructure.Data;

namespace Tailorly.Tests.Infrastructure;

/// <summary>Arranging the shared sourcing tables for integration tests (tests in the collection run one at a time).</summary>
public static class SourcesTestData
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public const string RemoteOkUrl = "https://remoteok.com/api";
    public const string GreenhouseApi = "https://boards-api.greenhouse.io/v1/boards/";

    public static string HimalayasUrl(string stream, int page) => $"https://himalayas.app/jobs/api/search?{stream}&sort=recent&page={page}";

    /// <summary>
    /// Empties jobs, companies and links (watch lists cascade), resets every source's health and restores the seed's
    /// enabled flags, terms dates and poll intervals. Clears fake HTTP routes and pending jobs.
    /// </summary>
    public static async Task ResetAsync(TailorlyAppFactory f, string cs)
    {
        f.Http.Reset();
        f.Jobs.Clear();
        await Db.ExecAsync(cs, """
            TRUNCATE jobs, company_ats_links, companies CASCADE;
            UPDATE source_health SET last_success_at = NULL, last_cursor = NULL, last_failure_at = NULL, last_error = NULL,
              consecutive_failures = 0, circuit_open_until = NULL, calls_this_month = 0, calls_period = NULL, last_run_stats = NULL;
            UPDATE job_sources SET enabled = key IN ('remoteok','greenhouse','byo'),
              terms_reviewed_at = CASE WHEN key IN ('remoteok','greenhouse') THEN '2026-10-25T00:00:00Z'::timestamptz END,
              poll_interval_minutes = CASE WHEN key = 'himalayas' THEN 1440 WHEN key = 'byo' THEN 1440 ELSE 360 END;
            """);
    }

    /// <summary>Enables a source (and marks its terms reviewed) directly in the database.</summary>
    public static Task EnableAsync(string cs, string key) => Db.ExecAsync(cs,
        "UPDATE job_sources SET enabled = true, terms_reviewed_at = coalesce(terms_reviewed_at, now()) WHERE key = @k", ("k", key));

    public static Task DisableAsync(string cs, string key) =>
        Db.ExecAsync(cs, "UPDATE job_sources SET enabled = false WHERE key = @k", ("k", key));

    /// <summary>Makes the next run read back to <paramref name="since"/> (the fixtures were captured on 2026-10-25).</summary>
    public static Task SetLastSuccessAsync(string cs, string key, DateTimeOffset since) => Db.ExecAsync(cs,
        "UPDATE source_health SET last_success_at = @t WHERE source_key = @k", ("k", key), ("t", since));

    /// <summary>Runs one source exactly as Hangfire would (own DI scope); returns its stats.</summary>
    public static async Task<RunStats?> FetchAsync(TailorlyAppFactory f, string key, bool manual = false)
    {
        await using var scope = f.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FetchSourceJob>().RunAsync(key, manual, Ct);
    }

    public static async Task<T> InScopeAsync<T>(TailorlyAppFactory f, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = f.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    /// <summary>Adds a board link (and its company) directly.</summary>
    public static async Task<Guid> AddLinkAsync(TailorlyAppFactory f, string platform, string token, string company,
        string origin = LinkOrigin.Seed)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var (link, _) = await scope.ServiceProvider.GetRequiredService<CompanyLinker>()
            .EnsureLinkAsync(platform, token, company, null, origin, Ct);
        return link.Id;
    }

    /// <summary>A Greenhouse list (no content) with the given jobs, in the real response's shape.</summary>
    public static string GreenhouseList(string company, params (long Id, string Title, string Location)[] jobs) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            jobs = jobs.Select(j => new
            {
                absolute_url = $"https://job-boards.greenhouse.io/x/jobs/{j.Id}",
                id = j.Id,
                title = j.Title,
                company_name = company,
                location = new { name = j.Location },
                updated_at = "2026-10-20T10:00:00-04:00",
                first_published = "2026-10-20T10:00:00-04:00",
            }),
            meta = new { total = jobs.Length },
        });

    /// <summary>A Greenhouse job detail with an entity-encoded description, in the real response's shape.</summary>
    public static string GreenhouseJob(string company, long id, string title, string location) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            absolute_url = $"https://job-boards.greenhouse.io/x/jobs/{id}",
            id,
            title,
            company_name = company,
            location = new { name = location },
            first_published = "2026-10-20T10:00:00-04:00",
            content = System.Net.WebUtility.HtmlEncode("<p>" + string.Concat(Enumerable.Repeat(
                "We build reliable payments infrastructure with .NET, PostgreSQL and Kubernetes. ", 12)) + "</p>"),
        });
}
