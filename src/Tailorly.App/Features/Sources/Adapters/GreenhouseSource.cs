using System.Text.Json;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Sources.Adapters;

/// <summary>
/// Greenhouse Job Board API (Path B). Verified against https://developers.greenhouse.io/job-board.html and live
/// responses on 2026-10-25 (fixtures in <c>Fixtures/Sources/greenhouse</c>):
/// <list type="bullet">
/// <item><c>GET /v1/boards/{token}/jobs</c> without <c>content</c> already returns id, title, company_name, location,
/// absolute_url, updated_at and first_published; <c>content=true</c> adds the description (HTML, entity-encoded),
/// departments and offices.</item>
/// <item><c>GET /v1/boards/{token}/jobs/{id}</c> returns one job with its content; an unknown board or job is a 404 with a
/// small JSON body.</item>
/// <item>No authentication, no documented rate limit, no structured salary. EU-hosted boards are not on this host.</item>
/// </list>
/// External ids are <c>{token}:{id}</c> so they are unique across boards.
/// </summary>
public sealed class GreenhouseSource(IHttpClientFactory http) : IBoardSource
{
    public const string Key = AtsPlatforms.Greenhouse;

    public string SourceKey => Key;

    public async Task<BoardListing> ListAsync(string boardToken, bool withContent, CancellationToken ct)
    {
        var token = AtsUrl.RequireToken(boardToken);
        using var doc = await SourceHttp.GetJsonAsync(Client(),
            $"v1/boards/{token}/jobs" + (withContent ? "?content=true" : ""), ct);
        return ParseList(doc.RootElement, token, withContent);
    }

    public async Task<RawJob?> GetJobAsync(string boardToken, string externalId, CancellationToken ct)
    {
        var token = AtsUrl.RequireToken(boardToken);
        var id = BoardIds.JobId(token, externalId);
        try
        {
            using var doc = await SourceHttp.GetJsonAsync(Client(), $"v1/boards/{token}/jobs/{id}", ct);
            return Map(doc.RootElement, token);
        }
        catch (SourceException ex) when (ex.Failure == SourceFailure.NotFound)
        {
            return null;
        }
    }

    private HttpClient Client() => http.CreateClient(SourceHttp.ClientName(Key));

    public static BoardListing ParseList(JsonElement root, string token, bool withContent)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            throw new SourceException(SourceFailure.BadPayload, "Greenhouse response has no jobs array");
        var list = new List<RawJob>();
        var skipped = 0;
        string? company = null;
        foreach (var j in jobs.EnumerateArray())
        {
            company ??= j.Str("company_name");
            if (Map(j, token) is { } job) list.Add(job);
            else skipped++;
        }
        return new BoardListing(company, list, HasDescriptions: withContent, skipped);
    }

    public static RawJob? Map(JsonElement j, string token)
    {
        var id = j.Str("id");
        var title = j.Str("title");
        var url = j.Str("absolute_url");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || !JobNormalizer.IsHttpUrl(url)) return null;
        var location = j.Obj("location")?.Str("name");

        return new RawJob
        {
            SourceKey = Key,
            ExternalId = BoardIds.External(token, id),
            Title = title,
            CompanyName = j.Str("company_name") ?? "",
            ApplyUrl = url!,
            SourceUrl = url!,
            LocationText = location,
            DescriptionHtml = j.Str("content"),
            PostedAt = j.Date("first_published") ?? j.Date("updated_at"),
            SourceMeta = new Dictionary<string, object?>
            {
                ["departments"] = j.Arr("departments").Select(d => d.Str("name")).Where(n => n is not null).ToList(),
                ["offices"] = j.Arr("offices").Select(d => d.Str("name")).Where(n => n is not null).ToList(),
                ["boardToken"] = token,
            },
        };
    }
}

/// <summary>Board external ids are <c>{token}:{id}</c>.</summary>
public static class BoardIds
{
    public static string External(string token, string id) => $"{token}:{id}";

    public static string JobId(string token, string externalId)
    {
        var prefix = token + ":";
        var id = externalId.StartsWith(prefix, StringComparison.Ordinal) ? externalId[prefix.Length..] : externalId;
        return Uri.EscapeDataString(id);
    }

    public static bool BelongsTo(string token, string externalId) => externalId.StartsWith(token + ":", StringComparison.Ordinal);
}
