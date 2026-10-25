using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Tailorly.App.Features.Health;

/// <summary><c>/healthz</c> = process alive; <c>/readyz</c> = database reachable (SDD §15.2). External APIs are not part of readiness.</summary>
public static class HealthEndpoints
{
    public const string DbTag = "db";

    public static void AddTailorlyHealthChecks(this IServiceCollection services, IConfiguration config)
    {
        services.AddHealthChecks()
            .AddNpgSql(_ => config.GetConnectionString("Default") ?? string.Empty, name: "postgres", tags: [DbTag],
                timeout: TimeSpan.FromSeconds(3));
    }

    public static void MapTailorlyHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false })
            .ExcludeFromDescription();
        app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains(DbTag) })
            .ExcludeFromDescription();
    }
}
