using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Sources.Adapters;

/// <summary>
/// Himalayas (Path A). Verified against https://himalayas.app/api and live responses on 2026-10-25 (fixtures in
/// <c>Fixtures/Sources/himalayas</c>). Differences from the PRD's notes, followed here:
/// <list type="bullet">
/// <item>Search (<c>/jobs/api/search</c>) pages with <c>page=N</c>, 20 per page; there is no cursor on search (only the
/// filterless browse feed has <c>nextCursor</c>). The stored cursor is the page number.</item>
/// <item><c>country=IN</c> already includes worldwide jobs, so the two searches are <c>country=IN&amp;exclude_worldwide=true</c>
/// (India-restricted) and <c>worldwide=true</c>: disjoint, and each maps straight to an eligibility.</item>
/// <item><c>sort=recent</c> is newest-first except for a few featured jobs pinned to the top of page 1.</item>
/// <item><c>locationRestrictions</c> are country names (strings; the docs show objects, both are read); empty = worldwide.
/// <c>timezoneRestrictions</c> are numeric offsets. <c>pubDate</c>/<c>expiryDate</c> are Unix seconds.</item>
/// <item>Salaries are in the stated <c>salaryPeriod</c> (annual, monthly, hourly…), not annual; the currency may be null.</item>
/// <item><c>guid</c> is the job's Himalayas URL. <c>applicationLink</c> is used unchanged for Apply (API terms).</item>
/// </list>
/// Ships disabled until Himalayas confirms in writing that a per-user matching service is permitted (PRD Appendix D).
/// </summary>
public sealed class HimalayasSource(IHttpClientFactory http, IOptions<SourcesOptions> options) : IFeedSource
{
    public const string Key = "himalayas";
    public const string IndiaStream = "country=IN&exclude_worldwide=true";
    public const string WorldwideStream = "worldwide=true";

    public string SourceKey => Key;
    public IReadOnlyList<string> Streams { get; } = [IndiaStream, WorldwideStream];

    public async Task<FeedPage> GetNewestPageAsync(string stream, string? cursor, CancellationToken ct)
    {
        if (!Streams.Contains(stream)) throw new ArgumentException("Unknown Himalayas stream.", nameof(stream));
        var page = int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : 1;
        var key = options.Value.Himalayas.ApiKey;
        using var doc = await SourceHttp.GetJsonAsync(http.CreateClient(SourceHttp.ClientName(Key)),
            $"jobs/api/search?{stream}&sort=recent&page={page}", ct,
            req => { if (!string.IsNullOrWhiteSpace(key)) req.Headers.Authorization = new("Bearer", key); });
        return Parse(doc.RootElement, page);
    }

    /// <summary>Maps one search response. An empty page ends the stream.</summary>
    public static FeedPage Parse(JsonElement root, int page)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            throw new SourceException(SourceFailure.BadPayload, "Himalayas response has no jobs array");
        var list = new List<RawJob>();
        var skipped = 0;
        foreach (var j in jobs.EnumerateArray())
        {
            if (Map(j) is { } job) list.Add(job);
            else skipped++;
        }
        var next = list.Count + skipped == 0 ? null : (page + 1).ToString(CultureInfo.InvariantCulture);
        return new FeedPage(list, next, skipped);
    }

    public static RawJob? Map(JsonElement j)
    {
        var guid = j.Str("guid");
        var title = j.Str("title");
        var company = j.Str("companyName");
        var apply = j.Str("applicationLink");
        if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(company)
            || !JobNormalizer.IsHttpUrl(apply)) return null;

        // Country names; the documented object form {alpha2, name, slug} is read too.
        var countries = j.Arr("locationRestrictions")
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.Str("name") ?? x.Str("alpha2"))
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).Distinct().ToList();
        var timezones = j.Strings("timezoneRestrictions");

        return new RawJob
        {
            SourceKey = Key,
            ExternalId = guid,
            Title = title,
            CompanyName = company,
            ApplyUrl = apply!,
            SourceUrl = JobNormalizer.IsHttpUrl(guid) ? guid : apply!,
            LocationText = countries.Count == 0 ? "Worldwide" : string.Join(", ", countries),
            WorkplaceType = "remote",
            CountryRestrictions = countries,
            DescriptionHtml = j.Str("description") ?? j.Str("excerpt"),
            EmploymentType = j.Str("employmentType"),
            SalaryMin = j.Num("minSalary"),
            SalaryMax = j.Num("maxSalary"),
            SalaryCurrency = j.Str("currency") ?? j.Str("salaryCurrency"),
            SalaryPeriod = SalaryParser.NormalizePeriod(j.Str("salaryPeriod")) ?? "year",
            PostedAt = j.Epoch("pubDate"),
            ExpiresAt = j.Epoch("expiryDate"),
            SourceMeta = new Dictionary<string, object?>
            {
                ["locationRestriction"] = countries,
                ["timezoneRestriction"] = timezones,
                ["isWorldwide"] = countries.Count == 0,
                ["seniority"] = j.Strings("seniority"),
                ["categories"] = j.Strings("categories"),
                ["companySlug"] = j.Str("companySlug"),
            },
        };
    }
}
