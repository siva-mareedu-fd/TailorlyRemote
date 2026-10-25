using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.RateLimiting;
using Polly.Timeout;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Sources;

/// <summary>Configuration section <c>Sources</c> (per source: <c>Sources:{key}:RequestsPerMinute</c>).</summary>
public sealed class SourcesOptions
{
    public string UserAgent { get; set; } = "TailorlyRemote/1.0 (+https://tailorly.example)";

    /// <summary>Per attempt (SDD §8.1: 15 s).</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Retries on 5xx/408/timeouts/network errors, with exponential back-off and jitter. 429/403 are not retried.</summary>
    public int MaxRetries { get; set; } = 2;

    public int RetryDelayMilliseconds { get; set; } = 1000;

    /// <summary>How long a source stays skipped after a 429/403 when it sends no longer <c>Retry-After</c> (PRD §7.2).</summary>
    public int CircuitOpenMinutes { get; set; } = 60;

    /// <summary>Concurrent requests per source (bulkhead).</summary>
    public int MaxConcurrentRequests { get; set; } = 2;

    /// <summary>Default per-source request rate; override with <c>Sources:{key}:RequestsPerMinute</c>.</summary>
    public int DefaultRequestsPerMinute { get; set; } = 60;

    /// <summary>Optional Himalayas API key (<c>Sources:Himalayas:ApiKey</c>), sent as a Bearer token for higher limits.</summary>
    public HimalayasOptions Himalayas { get; set; } = new();

    public sealed class HimalayasOptions
    {
        public string? ApiKey { get; set; }
    }
}

/// <summary>Configuration section <c>Fetch</c> (PRD §7.1b).</summary>
public sealed class FetchOptions
{
    /// <summary>Page cap per stream per run.</summary>
    public int MaxPagesPerRun { get; set; } = 50;

    /// <summary>First run (no <c>last_success_at</c>): how far back to read.</summary>
    public int FirstRunBackfillDays { get; set; } = 14;

    /// <summary>
    /// When a Greenhouse board has more new jobs than this, read the list once with <c>content=true</c> instead of one
    /// detail call per new job (a first poll of a 900-job board would otherwise make 900 calls).
    /// </summary>
    public int BoardBulkDetailThreshold { get; set; } = 25;
}

/// <summary>
/// One named <see cref="HttpClient"/> per source (SDD §8.1): fixed base address, a host allowlist checked on every attempt,
/// no redirects, a <c>User-Agent</c>, and a resilience pipeline (bulkhead, rate limiter, retry with jitter on
/// 5xx/timeouts, per-attempt timeout). The per-source circuit is persisted in <c>source_health.circuit_open_until</c> so it
/// survives restarts and shows in the admin console; 429/403 open it.
/// </summary>
public static class SourceHttp
{
    public static string ClientName(string sourceKey) => "source:" + sourceKey;

    /// <summary>Source key → the only hosts its client may call.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AllowedHosts = new Dictionary<string, string[]>
    {
        ["himalayas"] = ["himalayas.app"],
        ["remoteok"] = ["remoteok.com"],
        ["jobicy"] = ["jobicy.com"],
        ["wwr"] = ["weworkremotely.com"],
        ["greenhouse"] = ["boards-api.greenhouse.io"],
        ["lever"] = ["api.lever.co"],
        ["ashby"] = ["api.ashbyhq.com"],
    };

    public static void AddSourceHttpClients(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SourcesOptions>(config.GetSection("Sources"));
        services.Configure<FetchOptions>(config.GetSection("Fetch"));
        services.AddSingleton<SourceCallCounter>();
        var o = config.GetSection("Sources").Get<SourcesOptions>() ?? new SourcesOptions();

        foreach (var (key, hosts) in AllowedHosts)
        {
            var perMinute = config.GetValue<int?>($"Sources:{key}:RequestsPerMinute") ?? o.DefaultRequestsPerMinute;
            var client = services.AddHttpClient(ClientName(key), c =>
                {
                    c.BaseAddress = new Uri("https://" + hosts[0] + "/");
                    c.Timeout = Timeout.InfiniteTimeSpan; // the resilience pipeline owns timeouts
                    c.DefaultRequestHeaders.UserAgent.ParseAdd(o.UserAgent);
                    c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    c.MaxResponseContentBufferSize = 20 * 1024 * 1024;
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                });
            // Order = outermost first: resilience, then the host check and the call counter on every attempt.
            client.AddResilienceHandler("source", b =>
                {
                    b.AddConcurrencyLimiter(o.MaxConcurrentRequests, queueLimit: 1000);
                    b.AddRateLimiter(new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, perMinute),
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 12,
                        QueueLimit = 10_000,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    }));
                    b.AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = o.MaxRetries,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        Delay = TimeSpan.FromMilliseconds(o.RetryDelayMilliseconds),
                        ShouldHandle = args => ValueTask.FromResult(args.Outcome switch
                        {
                            { Exception: HttpRequestException or TimeoutRejectedException } => true,
                            { Result: { } r } => (int)r.StatusCode >= 500 || r.StatusCode == HttpStatusCode.RequestTimeout,
                            _ => false,
                        }),
                    });
                    b.AddTimeout(TimeSpan.FromSeconds(o.TimeoutSeconds));
                });
            client.AddHttpMessageHandler(_ => new HostAllowlistHandler(hosts))
                .AddHttpMessageHandler(sp => new CallCountingHandler(key, sp.GetRequiredService<SourceCallCounter>()));
        }
    }

    /// <summary>
    /// GET and parse JSON, mapping every failure to a <see cref="SourceException"/> (PRD §7.2): 404/410 → NotFound,
    /// 429/403 → Throttled (with <c>Retry-After</c>), any other non-2xx or network/timeout error → Unavailable, unparsable
    /// JSON → BadPayload. Payloads never appear in messages.
    /// </summary>
    public static async Task<JsonDocument> GetJsonAsync(HttpClient http, string relativeUrl, CancellationToken ct,
        Action<HttpRequestMessage>? configure = null)
    {
        using var res = await SendAsync(http, relativeUrl, ct, configure);
        try
        {
            await using var body = await res.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(body, new JsonDocumentOptions { MaxDepth = 64 }, ct);
        }
        catch (JsonException ex)
        {
            throw new SourceException(SourceFailure.BadPayload, "Response is not valid JSON", (int)res.StatusCode, inner: ex);
        }
    }

    /// <summary>GET a text body (RSS).</summary>
    public static async Task<string> GetTextAsync(HttpClient http, string relativeUrl, CancellationToken ct)
    {
        using var res = await SendAsync(http, relativeUrl, ct, r => r.Headers.Accept.ParseAdd("application/rss+xml"));
        return await res.Content.ReadAsStringAsync(ct);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, string relativeUrl, CancellationToken ct,
        Action<HttpRequestMessage>? configure)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        configure?.Invoke(req);
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or RateLimiterRejectedException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new SourceException(SourceFailure.Unavailable, ex is TimeoutRejectedException or TaskCanceledException
                ? "Timed out" : "Network error: " + ex.GetType().Name, inner: ex);
        }

        var status = (int)res.StatusCode;
        if (res.IsSuccessStatusCode) return res;
        var retryAfter = RetryAfter(res);
        res.Dispose();
        throw status switch
        {
            404 or 410 => new SourceException(SourceFailure.NotFound, $"HTTP {status}", status),
            429 or 403 => new SourceException(SourceFailure.Throttled, $"HTTP {status}", status, retryAfter),
            _ => new SourceException(SourceFailure.Unavailable, $"HTTP {status}", status),
        };
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage res)
    {
        if (res.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } d) return d;
            if (ra.Date is { } at) return at - DateTimeOffset.UtcNow;
        }
        return null;
    }

    // ---------- tolerant JSON readers (unknown fields ignored, missing fields null) ----------

    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    public static decimal? Num(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String
            && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public static bool? Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];

    /// <summary>Strings of an array (numbers rendered as text); other kinds skipped.</summary>
    public static List<string> Strings(this JsonElement e, string name) =>
        e.Arr(name).Select(x => x.ValueKind switch
            {
                JsonValueKind.String => x.GetString(),
                JsonValueKind.Number => x.GetRawText(),
                _ => null,
            })
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();

    public static DateTimeOffset? Date(this JsonElement e, string name) =>
        DateTimeOffset.TryParse(e.Str(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUniversalTime() : null;

    /// <summary>Unix time in seconds (or milliseconds when the value is clearly too large for seconds).</summary>
    public static DateTimeOffset? Epoch(this JsonElement e, string name)
    {
        if (e.Num(name) is not { } n || n <= 0) return null;
        try
        {
            return n > 100_000_000_000m
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)n)
                : DateTimeOffset.FromUnixTimeSeconds((long)n);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>Refuses any request whose host is not on the source's allowlist (defence in depth for SSRF; SDD §14.2).</summary>
public sealed class HostAllowlistHandler(string[] hosts) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0
            || !hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new SourceException(SourceFailure.Unavailable, "Blocked: host is not on the source allowlist");
        return base.SendAsync(request, ct);
    }
}

/// <summary>Counts real outbound attempts per source for <c>source_health.calls_this_month</c>.</summary>
public sealed class CallCountingHandler(string sourceKey, SourceCallCounter counter) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        counter.Increment(sourceKey);
        return base.SendAsync(request, ct);
    }
}

public sealed class SourceCallCounter
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public void Increment(string sourceKey) => _counts.AddOrUpdate(sourceKey, 1, (_, n) => n + 1);

    /// <summary>Calls since the last <see cref="Take"/> for this source.</summary>
    public int Take(string sourceKey) => _counts.TryRemove(sourceKey, out var n) ? n : 0;
}
