using Microsoft.AspNetCore.Mvc;
using Tailorly.Core;

namespace Tailorly.App.Api;

/// <summary>RFC 7807 helpers. Every error body carries a stable <c>code</c> and a <c>traceId</c> (the correlation id).</summary>
public static class Problems
{
    public const string TypeBase = "https://tailorly.example/errors/";

    public static void AddTailorlyProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            var pd = ctx.ProblemDetails;
            var status = pd.Status ?? ctx.HttpContext.Response.StatusCode;
            if (status == StatusCodes.Status405MethodNotAllowed)
            {
                // A wrong method on an existing route is reported like an unknown route (405 is not in ApiDesign §1.2).
                status = StatusCodes.Status404NotFound;
                ctx.HttpContext.Response.StatusCode = status;
                ctx.HttpContext.Response.Headers.Remove("Allow");
                pd.Status = status;
                pd.Title = TitleForStatus(status);
            }
            if (!pd.Extensions.ContainsKey("code"))
                pd.Extensions["code"] = CodeForStatus(status);
            var code = (string)pd.Extensions["code"]!;
            pd.Type = TypeBase + code.ToLowerInvariant().Replace('_', '-');
            pd.Title ??= TitleForStatus(status);
            pd.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
            pd.Extensions.Remove("requestId");
            pd.Instance = null;
        });
        services.AddExceptionHandler<ApiExceptionHandler>();
    }

    public static async Task WriteAsync(HttpContext http, int status, string code, string title,
        string? detail = null, IReadOnlyDictionary<string, object?>? extra = null)
    {
        http.Response.StatusCode = status;
        var pd = new ProblemDetails { Status = status, Title = title, Detail = detail };
        pd.Extensions["code"] = code;
        if (extra is not null) pd.Extensions["extra"] = extra;
        var writer = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        await writer.WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = pd });
    }

    public static string CodeForStatus(int status) => status switch
    {
        400 => ErrorCodes.BadRequest,
        401 => ErrorCodes.Unauthenticated,
        403 => ErrorCodes.Forbidden,
        404 => ErrorCodes.NotFound,
        413 => ErrorCodes.FileTooLarge,
        415 => ErrorCodes.UnsupportedFileType,
        422 => ErrorCodes.ValidationFailed,
        429 => ErrorCodes.RateLimited,
        503 => ErrorCodes.ServiceUnavailable,
        _ when status >= 500 => ErrorCodes.InternalError,
        _ => ErrorCodes.BadRequest,
    };

    private static string TitleForStatus(int status) => status switch
    {
        400 => "Bad request",
        401 => "Sign in required",
        403 => "Forbidden",
        404 => "Not found",
        413 => "Request too large",
        415 => "Unsupported media type",
        422 => "Validation failed",
        429 => "Too many requests",
        503 => "Service unavailable",
        _ => "Something went wrong",
    };
}
