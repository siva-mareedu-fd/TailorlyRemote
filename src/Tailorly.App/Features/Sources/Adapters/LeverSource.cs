using System.Text;
using System.Text.Json;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Sources.Adapters;

/// <summary>
/// Lever postings API (Path B, SHOULD). Verified against https://github.com/lever/postings-api and live responses on
/// 2026-10-25 (fixtures in <c>Fixtures/Sources/lever</c>): <c>GET /v0/postings/{site}?mode=json</c> returns every published
/// posting with its description, lists and closing text, so no detail calls are needed. <c>createdAt</c> (not in the
/// README) is epoch milliseconds; <c>salaryRange</c> is optional; <c>workplaceType</c> is
/// unspecified/on-site/remote/hybrid. An unknown site is a 404. The README notes published postings "may be scraped by
/// third parties"; no GET rate limit is documented, so the client is limited to about one request per second.
/// </summary>
public sealed class LeverSource(IHttpClientFactory http) : IBoardSource
{
    public const string Key = AtsPlatforms.Lever;

    public string SourceKey => Key;

    public async Task<BoardListing> ListAsync(string boardToken, bool withContent, CancellationToken ct)
    {
        var token = AtsUrl.RequireToken(boardToken);
        using var doc = await SourceHttp.GetJsonAsync(http.CreateClient(SourceHttp.ClientName(Key)), $"v0/postings/{token}?mode=json", ct);
        return ParseList(doc.RootElement, token);
    }

    /// <summary>Never called: the list already carries descriptions.</summary>
    public Task<RawJob?> GetJobAsync(string boardToken, string externalId, CancellationToken ct) => Task.FromResult<RawJob?>(null);

    public static BoardListing ParseList(JsonElement root, string token)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new SourceException(SourceFailure.BadPayload, "Lever response is not an array");
        var list = new List<RawJob>();
        var skipped = 0;
        foreach (var j in root.EnumerateArray())
        {
            if (Map(j, token) is { } job) list.Add(job);
            else skipped++;
        }
        return new BoardListing(null, list, HasDescriptions: true, skipped);
    }

    public static RawJob? Map(JsonElement j, string token)
    {
        var id = j.Str("id");
        var title = j.Str("text");
        var hosted = j.Str("hostedUrl");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || !JobNormalizer.IsHttpUrl(hosted)) return null;
        var categories = j.Obj("categories");
        var apply = j.Str("applyUrl");
        var salary = j.Obj("salaryRange");

        var html = new StringBuilder(j.Str("description") ?? "");
        foreach (var l in j.Arr("lists"))
            html.Append("<h3>").Append(l.Str("text")).Append("</h3><ul>").Append(l.Str("content")).Append("</ul>");
        html.Append(j.Str("additional"));

        return new RawJob
        {
            SourceKey = Key,
            ExternalId = BoardIds.External(token, id),
            Title = title,
            CompanyName = "",
            ApplyUrl = JobNormalizer.IsHttpUrl(apply) ? apply! : hosted!,
            SourceUrl = hosted!,
            LocationText = categories?.Str("location"),
            WorkplaceType = j.Str("workplaceType") is { } w && w != "unspecified" ? w : null,
            DescriptionHtml = html.ToString(),
            EmploymentType = categories?.Str("commitment"),
            SalaryMin = salary?.Num("min"),
            SalaryMax = salary?.Num("max"),
            SalaryCurrency = salary?.Str("currency"),
            SalaryPeriod = SalaryParser.NormalizePeriod(salary?.Str("interval")),
            PostedAt = j.Epoch("createdAt"),
            SourceMeta = new Dictionary<string, object?>
            {
                ["team"] = categories?.Str("team"),
                ["department"] = categories?.Str("department"),
                ["allLocations"] = categories is { } c ? c.Strings("allLocations") : [],
                ["country"] = j.Str("country"),
                ["boardToken"] = token,
            },
        };
    }
}
