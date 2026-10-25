using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Tailorly.Tests.Infrastructure;

/// <summary>
/// Test HTTP client that behaves like the SPA: it remembers the readable <c>XSRF-TOKEN</c> cookie from any response
/// and sends it as <c>X-XSRF-TOKEN</c> on unsafe requests. Cookies (auth, antiforgery) are kept by the inner client.
/// </summary>
public sealed partial class ApiClient(HttpClient http)
{
    private string? _xsrf;

    public HttpClient Http => http;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task RefreshCsrfAsync()
    {
        var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/csrf"));
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => SendJsonAsync(HttpMethod.Post, path, body);
    public Task<HttpResponseMessage> PutAsync(string path, object? body) => SendJsonAsync(HttpMethod.Put, path, body);
    public Task<HttpResponseMessage> PatchAsync(string path, object? body) => SendJsonAsync(HttpMethod.Patch, path, body);
    public Task<HttpResponseMessage> DeleteAsync(string path) => SendAsync(new HttpRequestMessage(HttpMethod.Delete, path));

    public Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, object? body)
    {
        var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body);
        return SendAsync(req);
    }

    public Task<HttpResponseMessage> UploadAsync(string path, string fileName, byte[] bytes, string contentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        return SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = form });
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req)
    {
        if (req.Method != HttpMethod.Get && req.Method != HttpMethod.Head && _xsrf is not null)
            req.Headers.Add("X-XSRF-TOKEN", _xsrf);
        var res = await http.SendAsync(req, Ct);
        if (res.Headers.TryGetValues("Set-Cookie", out var cookies))
            foreach (var c in cookies.Where(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)))
                _xsrf = Uri.UnescapeDataString(c.Split(';')[0]["XSRF-TOKEN=".Length..]);
        return res;
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync(Ct);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<string> CodeAsync(HttpResponseMessage res) => (await JsonAsync(res)).GetProperty("code").GetString()!;

    public static Task<string> TextAsync(HttpResponseMessage res) => res.Content.ReadAsStringAsync(Ct);

    /// <summary>Extracts <c>userId</c> and <c>token</c> from a link like <c>https://tailorly.test/verify-email?userId=…&amp;token=…</c>.</summary>
    public static (Guid UserId, string Token) ParseLink(string text, string path)
    {
        var m = Regex.Match(text, Regex.Escape("https://tailorly.test" + path) + @"\?userId=([0-9a-f-]+)&token=([^\s""<]+)");
        m.Success.Should().BeTrue($"the email should contain a {path} link");
        return (Guid.Parse(m.Groups[1].Value), Uri.UnescapeDataString(m.Groups[2].Value));
    }
}
