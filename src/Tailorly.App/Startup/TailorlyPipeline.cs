using Serilog;
using Tailorly.App.Api;
using Tailorly.App.Features.Accounts;
using Tailorly.App.Features.Admin;
using Tailorly.App.Features.Digests;
using Tailorly.App.Features.Jobs;
using Tailorly.App.Features.Health;
using Tailorly.App.Features.Meta;
using Tailorly.App.Features.Preferences;
using Tailorly.App.Features.Resumes;
using Tailorly.App.Features.Tailoring;
using Tailorly.App.Features.Usage;
using Tailorly.App.Infrastructure.Jobs;

namespace Tailorly.App.Startup;

/// <summary>HTTP pipeline. Order matters: correlation id first so every log line and error body carries it.</summary>
public static class TailorlyPipeline
{
    public static WebApplication UseTailorly(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        if (!app.Environment.IsDevelopment())
            app.UseHsts();

        // Outside the exception handler, so it logs the final status (expected 4xx problems are not errors).
        app.UseSerilogRequestLogging(o =>
        {
            // Method, path, status and elapsed time only; never bodies or query strings.
            o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";
            o.GetLevel = (http, _, ex) => ex is not null || http.Response.StatusCode >= 500
                ? Serilog.Events.LogEventLevel.Error
                : Serilog.Events.LogEventLevel.Information;
        });
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseTailorlyCsrf();

        app.MapTailorlyHealthEndpoints();

        var api = app.MapGroup("/api/v1").RequireRateLimiting(RateLimiting.ApiDefault);
        api.MapMetaEndpoints();
        api.MapCsrfEndpoints();
        api.MapAuthEndpoints();
        api.MapMeEndpoints();
        api.MapResumeEndpoints();
        api.MapConfirmedSkillsEndpoints();
        api.MapUsageEndpoints();
        api.MapPreferencesEndpoints();
        api.MapWatchedCompaniesEndpoints();
        api.MapJobEndpoints();
        api.MapDigestEndpoints();
        api.MapUserJobEndpoints();
        api.MapTailoringEndpoints();
        api.MapEmailLinkEndpoints();
        api.MapAdminGroup().MapAdminReports();

        app.MapTailorlyHangfireDashboard();

        if (app.Environment.IsDevelopment())
            app.MapOpenApi("/openapi/{documentName}.json");

        return app;
    }
}
