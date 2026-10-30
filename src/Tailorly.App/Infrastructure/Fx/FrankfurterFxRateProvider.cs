using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Http.Resilience;
using Tailorly.App.Features.Sources;
using Tailorly.Core.Ports;

namespace Tailorly.App.Infrastructure.Fx;

public sealed class FxOptions
{
    /// <summary><c>Frankfurter</c> (daily public rates, no key) or <c>Static</c> (configured rates only).</summary>
    public string Provider { get; set; } = "Frankfurter";

    /// <summary>How long a fetched rate table is used before it is refreshed (rates change once a day).</summary>
    public int CacheHours { get; set; } = 12;

    /// <summary>After a failed fetch, wait this long before trying again (a stale table or the static rates are used meanwhile).</summary>
    public int RetryMinutes { get; set; } = 15;
}

/// <summary>
/// Daily-cached public FX rates from Frankfurter v2 (<c>GET https://api.frankfurter.dev/v2/rates?base=USD</c>; no key, no
/// quotas, commercial use and caching allowed; verified 2026-10-30, fixture in the tests). One table of USD-based rates
/// is fetched at most every <see cref="FxOptions.CacheHours"/> and any pair is derived from it. When the API cannot be
/// reached the last table is kept; with none, the configured static rates answer. Used only to display "≈ INR" and to
/// compare salaries with the user's range.
/// </summary>
public sealed partial class FrankfurterFxRateProvider(
    IHttpClientFactory http,
    IMemoryCache cache,
    StaticFxRateProvider fallback,
    Microsoft.Extensions.Options.IOptions<FxOptions> options,
    TimeProvider clock,
    ILogger<FrankfurterFxRateProvider> logger) : IFxRateProvider
{
    public const string ClientName = "fx:frankfurter";
    public const string Host = "api.frankfurter.dev";
    private const string CacheKey = "fx:frankfurter:usd";
    private static readonly SemaphoreSlim Refresh = new(1, 1);

    private sealed record Table(IReadOnlyDictionary<string, decimal> PerUsd, DateTimeOffset FetchedAt, DateTimeOffset NextTryAt);

    public async Task<decimal?> GetRateAsync(string from, string to, CancellationToken ct)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return 1m;
        var table = await GetTableAsync(ct);
        if (table is not null && table.PerUsd.TryGetValue(from.ToUpperInvariant(), out var f) && f > 0
            && table.PerUsd.TryGetValue(to.ToUpperInvariant(), out var t))
            return t / f;
        return await fallback.GetRateAsync(from, to, ct);
    }

    private async Task<Table?> GetTableAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        cache.TryGetValue(CacheKey, out Table? current);
        if (current is not null && now < current.NextTryAt) return current;

        await Refresh.WaitAsync(ct);
        try
        {
            cache.TryGetValue(CacheKey, out current);
            if (current is not null && now < current.NextTryAt) return current;
            try
            {
                var rates = await FetchAsync(ct);
                current = new Table(rates, now, now.AddHours(Math.Max(1, options.Value.CacheHours)));
                LogFetched(logger, rates.Count);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidDataException
                                           or Polly.CircuitBreaker.BrokenCircuitException or Polly.Timeout.TimeoutRejectedException
                                           or Tailorly.Core.Ports.SourceException && !ct.IsCancellationRequested)
            {
                LogFailed(logger, ex.GetType().Name);
                // Keep the stale table (if any) and try again a little later.
                current = current is null ? null : current with { NextTryAt = now.AddMinutes(Math.Max(1, options.Value.RetryMinutes)) };
                if (current is null)
                {
                    cache.Set(CacheKey, new Table(new Dictionary<string, decimal>(), now, now.AddMinutes(Math.Max(1, options.Value.RetryMinutes))),
                        TimeSpan.FromDays(3));
                    return null;
                }
            }
            cache.Set(CacheKey, current, TimeSpan.FromDays(3));
            return current;
        }
        finally
        {
            Refresh.Release();
        }
    }

    private async Task<Dictionary<string, decimal>> FetchAsync(CancellationToken ct)
    {
        using var res = await http.CreateClient(ClientName).GetAsync("v2/rates?base=USD", ct);
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"HTTP {(int)res.StatusCode}");
        await using var body = await res.Content.ReadAsStreamAsync(ct);
        return Parse(await JsonDocument.ParseAsync(body, cancellationToken: ct));
    }

    /// <summary>
    /// Reads <c>[{"date","base","quote","rate"}, …]</c> into units per USD (USD itself is 1). Tolerant: unknown fields are
    /// ignored and malformed rows skipped; a response without any usable row is invalid.
    /// </summary>
    internal static Dictionary<string, decimal> Parse(JsonDocument doc)
    {
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Expected an array of rates.");
            var rates = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["USD"] = 1m };
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!row.TryGetProperty("base", out var b) || b.GetString() != "USD") continue;
                if (!row.TryGetProperty("quote", out var q) || q.GetString() is not { Length: 3 } quote) continue;
                if (!row.TryGetProperty("rate", out var r) || r.ValueKind != JsonValueKind.Number || !r.TryGetDecimal(out var rate) || rate <= 0)
                    continue;
                rates[quote.ToUpperInvariant()] = rate;
            }
            if (rates.Count <= 1) throw new InvalidDataException("No usable rates in the response.");
            return rates;
        }
    }

    /// <summary>
    /// A fixed base address, no redirects, the standard resilience pipeline, then (inside it, on every attempt) the
    /// same host allowlist the job sources use.
    /// </summary>
    public static void AddClient(IServiceCollection services)
    {
        var client = services.AddHttpClient(ClientName, c =>
            {
                c.BaseAddress = new Uri($"https://{Host}/");
                c.Timeout = Timeout.InfiniteTimeSpan; // the resilience pipeline owns timeouts
                c.DefaultRequestHeaders.UserAgent.ParseAdd("TailorlyRemote/1.0");
                c.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        client.AddStandardResilienceHandler(o =>
        {
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        });
        client.AddHttpMessageHandler(_ => new HostAllowlistHandler([Host]));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "FX rates refreshed: {Count} currencies")]
    private static partial void LogFetched(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "FX rates could not be refreshed ({ExceptionType}); using cached or static rates")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
