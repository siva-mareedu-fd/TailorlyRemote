using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Tailorly.Core;

namespace Tailorly.App.Api;

/// <summary>Maps exceptions to problem details. Unknown exceptions never leak their message to the client.</summary>
public sealed partial class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var pd = new ProblemDetails();
        switch (exception)
        {
            case ApiException api:
                pd.Status = api.Status;
                pd.Title = api.Title;
                pd.Detail = api.Detail;
                pd.Extensions["code"] = api.Code;
                if (api.Extra is not null) pd.Extensions["extra"] = api.Extra;
                if (api.Errors is not null) pd.Extensions["errors"] = api.Errors;
                break;
            case BadHttpRequestException bad:
                pd.Status = bad.StatusCode;
                pd.Extensions["code"] = Problems.CodeForStatus(bad.StatusCode);
                break;
            default:
                // Log the exception type and stack only through the exception object; no request bodies.
                LogUnhandled(logger, exception, exception.GetType().Name);
                pd.Status = StatusCodes.Status500InternalServerError;
                pd.Title = "Something went wrong";
                pd.Detail = "Please try again. If it keeps happening, quote the trace id.";
                pd.Extensions["code"] = ErrorCodes.InternalError;
                break;
        }

        http.Response.StatusCode = pd.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = pd,
            Exception = exception,
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception {ExceptionType}")]
    private static partial void LogUnhandled(ILogger logger, Exception ex, string exceptionType);
}
