using Microsoft.AspNetCore.Antiforgery;
using Tailorly.Core;

namespace Tailorly.App.Api;

/// <summary>Marks an endpoint that authenticates by a signed token instead of the cookie (e.g. one-click unsubscribe).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class SkipCsrfAttribute : Attribute;

/// <summary>
/// Double-submit CSRF (ApiDesign §1): the SPA reads the <c>XSRF-TOKEN</c> cookie and sends it as
/// <c>X-XSRF-TOKEN</c> on every unsafe request under <c>/api</c>. Missing or invalid → 403 CSRF_INVALID.
/// </summary>
public static class CsrfProtection
{
    public const string HeaderName = "X-XSRF-TOKEN";
    public const string ReadableCookieName = "XSRF-TOKEN";

    public static void AddTailorlyAntiforgery(this IServiceCollection services)
    {
        services.AddAntiforgery(o =>
        {
            o.HeaderName = HeaderName;
            o.Cookie.Name = "__Host-tailorly-af";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.Path = "/";
        });
    }

    /// <summary>Issues the antiforgery cookie pair. Call on SPA load and again after sign-in (tokens are bound to the user).</summary>
    public static void IssueTokens(HttpContext http)
    {
        var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Cookies.Append(ReadableCookieName, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });
    }

    /// <summary>Validates unsafe API requests. Runs after routing so endpoint metadata is available.</summary>
    public static IApplicationBuilder UseTailorlyCsrf(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (RequiresCheck(http))
        {
            var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
            if (!await antiforgery.IsRequestValidAsync(http))
            {
                await Problems.WriteAsync(http, StatusCodes.Status403Forbidden, ErrorCodes.CsrfInvalid,
                    "Security token missing or expired", "Refresh the page and try again.");
                return;
            }
        }
        await next(http);
    });

    private static bool RequiresCheck(HttpContext http)
    {
        if (!http.Request.Path.StartsWithSegments("/api")) return false;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) ||
            HttpMethods.IsOptions(http.Request.Method) || HttpMethods.IsTrace(http.Request.Method))
            return false;
        return http.GetEndpoint()?.Metadata.GetMetadata<SkipCsrfAttribute>() is null;
    }
}
