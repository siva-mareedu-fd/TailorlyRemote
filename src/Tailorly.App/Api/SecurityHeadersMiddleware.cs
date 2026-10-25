namespace Tailorly.App.Api;

/// <summary>
/// HTTP hardening headers (SDD §14.4). HSTS is added by <c>UseHsts</c> outside Development.
/// The Hangfire dashboard needs inline scripts and styles, so <c>/admin/jobs</c> gets a relaxed CSP
/// (see DECISIONS.md); everything else uses the strict policy.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string StrictCsp =
        "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; " +
        "img-src 'self' data:; font-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'";

    public const string DashboardCsp =
        "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; " +
        "img-src 'self' data:; font-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'";

    public Task InvokeAsync(HttpContext http)
    {
        var path = http.Request.Path;
        http.Response.OnStarting(() =>
        {
            var h = http.Response.Headers;
            h.ContentSecurityPolicy = path.StartsWithSegments("/admin/jobs") ? DashboardCsp : StrictCsp;
            h.XContentTypeOptions = "nosniff";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h.XFrameOptions = "DENY";
            if (path.StartsWithSegments("/api"))
                h.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        return next(http);
    }
}
