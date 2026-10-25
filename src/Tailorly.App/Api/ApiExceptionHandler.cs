using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
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
                foreach (var (name, value) in api.Headers) http.Response.Headers[name] = value;
                break;
            case BadHttpRequestException bad:
                pd.Status = bad.StatusCode;
                pd.Extensions["code"] = Problems.CodeForStatus(bad.StatusCode);
                break;
            case Exception when IsDatabaseUnavailable(exception):
                // SDD §15.1: DB unavailable → 503; the client may retry.
                LogDatabaseUnavailable(logger, exception, exception.GetType().Name);
                pd.Status = StatusCodes.Status503ServiceUnavailable;
                pd.Title = "Service temporarily unavailable";
                pd.Detail = "Please try again in a moment.";
                pd.Extensions["code"] = ErrorCodes.ServiceUnavailable;
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

    // Connection refused, timeouts and other transient Npgsql failures, raw or wrapped once by EF.
    private static bool IsDatabaseUnavailable(Exception ex) =>
        ex is NpgsqlException { IsTransient: true } || ex.InnerException is NpgsqlException { IsTransient: true };

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception {ExceptionType}")]
    private static partial void LogUnhandled(ILogger logger, Exception ex, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Database unavailable {ExceptionType}")]
    private static partial void LogDatabaseUnavailable(ILogger logger, Exception ex, string exceptionType);
}
