using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Tailorly.Tests.Infrastructure;

/// <summary>
/// The primary handler of every source <see cref="HttpClient"/> in tests: no request ever leaves the machine. Routes are
/// exact absolute URLs; every attempt is recorded (after the app's resilience pipeline and host allowlist), so tests can
/// assert exactly which URLs were called and how often. An unknown URL answers 404 and is recorded in
/// <see cref="Unmatched"/>.
/// </summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>> _routes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _sticky = new(StringComparer.Ordinal);

    public ConcurrentQueue<Uri> Requests { get; } = new();
    public ConcurrentQueue<Uri> Unmatched { get; } = new();

    public void Reset()
    {
        _routes.Clear();
        _sticky.Clear();
        Requests.Clear();
        Unmatched.Clear();
    }

    /// <summary>Every request to <paramref name="url"/> gets this response.</summary>
    public FakeHttp Always(string url, int status, string body, string contentType = "application/json",
        IDictionary<string, string>? headers = null)
    {
        _sticky[url] = (_, _) => Task.FromResult(Build(status, body, contentType, headers));
        return this;
    }

    public FakeHttp AlwaysFixture(string url, string fixture, string contentType = "application/json") =>
        Always(url, 200, Fixtures.Read(fixture), contentType);

    /// <summary>Custom behaviour for every request (delays, counting…). The token is the attempt's (Polly timeouts cancel it).</summary>
    public FakeHttp Always(string url, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _sticky[url] = handler;
        return this;
    }

    /// <summary>The next request only (queued before any <see cref="Always(string,int,string,string,IDictionary{string,string})"/> route).</summary>
    public FakeHttp Once(string url, int status, string body, string contentType = "application/json")
    {
        _routes.GetOrAdd(url, _ => new()).Enqueue((_, _) => Task.FromResult(Build(status, body, contentType, null)));
        return this;
    }

    public int Count(string url) => Requests.Count(u => u.AbsoluteUri == url);
    public int CountPrefix(string prefix) => Requests.Count(u => u.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Enqueue(request.RequestUri);
        if (_routes.TryGetValue(url, out var queue) && queue.TryDequeue(out var once)) return await once(request, ct);
        if (_sticky.TryGetValue(url, out var handler)) return await handler(request, ct);
        Unmatched.Enqueue(request.RequestUri);
        return Build(404, "{\"status\":404,\"error\":\"Not found (fake)\"}", "application/json", null);
    }

    public static HttpResponseMessage Build(int status, string body, string contentType = "application/json",
        IDictionary<string, string>? headers = null)
    {
        var res = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) res.Headers.TryAddWithoutValidation(k, v);
        return res;
    }
}

/// <summary>Real source responses captured on 2026-10-25 (see <c>Fixtures/Sources/README.md</c>).</summary>
public static class Fixtures
{
    public static string Path(string relative) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sources", relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public static string Read(string relative) => File.ReadAllText(Path(relative), Encoding.UTF8);
}
