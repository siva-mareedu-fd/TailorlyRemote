using System.Text.Json;
using System.Text.RegularExpressions;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Sources.Adapters;

/// <summary>
/// Remote OK (Path A). Verified against https://remoteok.com/api and https://remoteok.com/legal on 2026-10-25 (fixture in
/// <c>Fixtures/Sources/remoteok</c>):
/// <list type="bullet">
/// <item>One JSON array of about 100 newest jobs; element 0 is the legal notice (<c>last_updated</c>, <c>legal</c>) and is
/// skipped (any element without an id/position is). No pagination, so a run is one request.</item>
/// <item>Terms: a followed (not nofollow) link to the job's Remote OK URL and the name "Remote OK" as source; no logo.</item>
/// <item><c>apply_url</c> points at Remote OK itself, never the employer, so apply-URL company discovery finds nothing.</item>
/// <item>Some descriptions are teasers ending in "..."; each ends with an anti-spam note whose tag encodes the
/// requester's IP. The note is removed before storing and teasers are marked partial (limited).</item>
/// <item><c>salary_min</c>/<c>salary_max</c> are annual USD; 0 means not given. <c>location</c> is often empty and the
/// remote flag is unverified (PRD §7.1).</item>
/// </list>
/// </summary>
public sealed partial class RemoteOkSource(IHttpClientFactory http) : IFeedSource
{
    public const string Key = "remoteok";
    public const string Stream = "latest";

    public string SourceKey => Key;
    public IReadOnlyList<string> Streams { get; } = [Stream];

    public async Task<FeedPage> GetNewestPageAsync(string stream, string? cursor, CancellationToken ct)
    {
        using var doc = await SourceHttp.GetJsonAsync(http.CreateClient(SourceHttp.ClientName(Key)), "api", ct);
        return Parse(doc.RootElement);
    }

    public static FeedPage Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new SourceException(SourceFailure.BadPayload, "Remote OK response is not an array");
        var list = new List<RawJob>();
        var skipped = 0;
        foreach (var j in root.EnumerateArray())
        {
            if (j.ValueKind == JsonValueKind.Object && j.TryGetProperty("legal", out _) && !j.TryGetProperty("position", out _))
                continue; // the terms notice
            if (Map(j) is { } job) list.Add(job);
            else skipped++;
        }
        return new FeedPage(list, NextCursor: null, skipped);
    }

    public static RawJob? Map(JsonElement j)
    {
        var id = j.Str("id");
        var title = RepairUtf8(j.Str("position"));
        var company = RepairUtf8(j.Str("company"));
        var url = j.Str("url");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(company)
            || !JobNormalizer.IsHttpUrl(url)) return null;
        var apply = j.Str("apply_url");
        var (description, partial) = CleanDescription(RepairUtf8(j.Str("description")));
        decimal? min = j.Num("salary_min") is decimal lo && lo > 0 ? lo : null;
        decimal? max = j.Num("salary_max") is decimal hi && hi > 0 ? hi : null;

        return new RawJob
        {
            SourceKey = Key,
            ExternalId = id,
            Title = title,
            CompanyName = company,
            ApplyUrl = JobNormalizer.IsHttpUrl(apply) ? apply! : url!,
            SourceUrl = url!,
            LocationText = string.IsNullOrWhiteSpace(j.Str("location")) ? null : RepairUtf8(j.Str("location")),
            DescriptionHtml = description,
            DescriptionIsPartial = partial,
            SalaryMin = min,
            SalaryMax = max,
            SalaryCurrency = min is null && max is null ? null : "USD",
            SalaryPeriod = "year",
            PostedAt = j.Epoch("epoch") ?? j.Date("date"),
            SourceMeta = new Dictionary<string, object?> { ["tags"] = j.Strings("tags") },
        };
    }

    private static readonly System.Text.Encoding StrictUtf8 = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>
    /// Remote OK sends UTF-8 that was decoded as Latin-1 once too often (an em dash arrives as <c>â\u0080\u0094</c>).
    /// When every character fits in one byte and those bytes are valid UTF-8, the text is re-decoded; otherwise it is kept.
    /// </summary>
    public static string? RepairUtf8(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.Any(c => c is >= (char)0x80 and <= (char)0xFF) || s.Any(c => c > (char)0xFF)) return s;
        var bytes = s.Select(c => (byte)c).ToArray();
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return s;
        }
    }

    /// <summary>Removes the per-requester anti-spam note; a remaining "..." at the end marks a teaser.</summary>
    public static (string? Html, bool Partial) CleanDescription(string? html)
    {
        if (string.IsNullOrEmpty(html)) return (html, false);
        var cleaned = SpamNote().Replace(html, "");
        var plain = HtmlText.ToPlainText(cleaned);
        return (cleaned, plain.EndsWith("...", StringComparison.Ordinal) || plain.EndsWith('…'));
    }

    [GeneratedRegex(@"Please mention the word\b.*?(?:see they're human|see they’re human)\.?", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex SpamNote();
}
