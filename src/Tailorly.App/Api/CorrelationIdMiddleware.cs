using System.Text.RegularExpressions;
using Serilog.Context;

namespace Tailorly.App.Api;

/// <summary>
/// Reads or creates <c>X-Correlation-Id</c>, returns it on the response, uses it as the request's
/// trace identifier (so it appears as <c>traceId</c> in problem details) and pushes it into the log context.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string LogProperty = "CorrelationId";

    private static readonly AsyncLocal<string?> CurrentId = new();

    /// <summary>The correlation id of the current request or background job, if any.</summary>
    public static string? Current
    {
        get => CurrentId.Value;
        set => CurrentId.Value = value;
    }

    public async Task InvokeAsync(HttpContext http)
    {
        var incoming = http.Request.Headers[HeaderName].ToString();
        var id = IsSafe(incoming) ? incoming : Guid.CreateVersion7().ToString("N");

        http.TraceIdentifier = id;
        Current = id;
        http.Response.OnStarting(() =>
        {
            http.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty(LogProperty, id))
        {
            await next(http);
        }
    }

    // Accept only short, plain ids from clients so the header cannot inject content into logs.
    private static bool IsSafe(string value) => value.Length is > 0 and <= 64 && SafeId().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeId();
}
