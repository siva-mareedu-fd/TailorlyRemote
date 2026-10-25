using System.Text.Json;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Sources.Adapters;

/// <summary>
/// Ashby public job posting API (Path B, SHOULD). Verified against
/// https://developers.ashbyhq.com/docs/public-job-posting-api and live responses on 2026-10-25 (fixtures in
/// <c>Fixtures/Sources/ashby</c>): <c>GET /posting-api/job-board/{name}?includeCompensation=true</c> lists every job with
/// <c>descriptionHtml</c>. Live responses carry a job <c>id</c> (not in the docs) and <c>compensation</c> per job (the docs'
/// example shows it at the top level). Board names are case-insensitive, so lower-case tokens work. Unlisted jobs
/// (<c>isListed: false</c>) are skipped. An unknown board is a 404 with a plain-text body.
/// </summary>
public sealed class AshbySource(IHttpClientFactory http) : IBoardSource
{
    public const string Key = AtsPlatforms.Ashby;

    public string SourceKey => Key;

    public async Task<BoardListing> ListAsync(string boardToken, bool withContent, CancellationToken ct)
    {
        var token = AtsUrl.RequireToken(boardToken);
        using var doc = await SourceHttp.GetJsonAsync(http.CreateClient(SourceHttp.ClientName(Key)),
            $"posting-api/job-board/{token}?includeCompensation=true", ct);
        return ParseList(doc.RootElement, token);
    }

    /// <summary>Never called: the list already carries descriptions.</summary>
    public Task<RawJob?> GetJobAsync(string boardToken, string externalId, CancellationToken ct) => Task.FromResult<RawJob?>(null);

    public static BoardListing ParseList(JsonElement root, string token)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            throw new SourceException(SourceFailure.BadPayload, "Ashby response has no jobs array");
        var list = new List<RawJob>();
        var skipped = 0;
        foreach (var j in jobs.EnumerateArray())
        {
            if (j.Bool("isListed") == false) continue;
            if (Map(j, token) is { } job) list.Add(job);
            else skipped++;
        }
        return new BoardListing(null, list, HasDescriptions: true, skipped);
    }

    public static RawJob? Map(JsonElement j, string token)
    {
        var id = j.Str("id");
        var title = j.Str("title");
        var url = j.Str("jobUrl");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || !JobNormalizer.IsHttpUrl(url)) return null;
        var apply = j.Str("applyUrl");
        var salary = j.Obj("compensation")?.Arr("summaryComponents")
            .FirstOrDefault(c => string.Equals(c.Str("compensationType"), "Salary", StringComparison.OrdinalIgnoreCase));
        var secondary = j.Arr("secondaryLocations").Select(s => s.Str("location")).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var location = j.Str("location");

        return new RawJob
        {
            SourceKey = Key,
            ExternalId = BoardIds.External(token, id),
            Title = title,
            CompanyName = "",
            ApplyUrl = JobNormalizer.IsHttpUrl(apply) ? apply! : url!,
            SourceUrl = url!,
            LocationText = secondary.Count == 0 ? location : string.Join("; ", new[] { location }.Concat(secondary).Where(s => s is not null)),
            WorkplaceType = j.Str("workplaceType"),
            DescriptionHtml = j.Str("descriptionHtml"),
            EmploymentType = j.Str("employmentType"),
            SalaryMin = salary?.Num("minValue"),
            SalaryMax = salary?.Num("maxValue"),
            SalaryCurrency = salary?.Str("currencyCode"),
            SalaryPeriod = salary is { } s ? SalaryParser.NormalizePeriod(s.Str("interval")) : null,
            PostedAt = j.Date("publishedAt"),
            SourceMeta = new Dictionary<string, object?>
            {
                ["department"] = j.Str("department"),
                ["team"] = j.Str("team"),
                ["isRemote"] = j.Bool("isRemote"),
                ["boardToken"] = token,
            },
        };
    }
}
