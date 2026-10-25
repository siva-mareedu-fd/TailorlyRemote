using Hangfire;
using Hangfire.Client;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Hangfire.Server;
using Serilog.Context;
using Tailorly.App.Api;
using Tailorly.App.Infrastructure.Data;

namespace Tailorly.App.Infrastructure.Jobs;

/// <summary>
/// Hangfire with PostgreSQL storage (schema <c>hangfire</c>), one default queue, server in the same process (SDD §11.4).
/// The schema is installed by <c>--migrate</c>, never on normal startup.
/// </summary>
public static class HangfireSetup
{
    public const string DashboardPath = "/admin/jobs";

    public static void AddTailorlyHangfire(this IServiceCollection services, IConfiguration config)
    {
        services.AddHangfire((sp, h) => h
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseFilter(new CorrelationIdJobFilter())
            .UseFilter(new AutomaticRetryAttribute { Attempts = 3 })
            .UsePostgreSqlStorage(
                o => o.UseNpgsqlConnection(config.GetConnectionString("Default")),
                new PostgreSqlStorageOptions
                {
                    SchemaName = MigrationRunner.HangfireSchema,
                    PrepareSchemaIfNecessary = false,
                }));

        // The build-time OpenAPI generator boots the host; it must not start a job server.
        var isDocGenerator = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
        if (config.GetValue("Hangfire:ServerEnabled", true) && !isDocGenerator)
        {
            services.AddHangfireServer(o =>
            {
                o.WorkerCount = config.GetValue("Hangfire:WorkerCount", 4);
                o.Queues = ["default"];
            });
        }
    }

    public static void MapTailorlyHangfireDashboard(this IEndpointRouteBuilder app)
    {
        app.MapHangfireDashboard(DashboardPath, new DashboardOptions
        {
            Authorization = [new AdminDashboardAuthorization()],
            DashboardTitle = "Tailorly jobs",
            AppPath = "/admin",
            DisplayStorageConnectionString = false,
        }).RequireAuthorization(AuthPolicies.Admin);
    }
}

/// <summary>Second line of defence behind the endpoint policy: only signed-in Admins see the dashboard.</summary>
public sealed class AdminDashboardAuthorization : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var user = context.GetHttpContext().User;
        return user.Identity?.IsAuthenticated == true && user.IsInRole(AppRole.Admin);
    }
}

/// <summary>Carries the request correlation id into jobs and their logs (SDD §15.2).</summary>
public sealed class CorrelationIdJobFilter : IClientFilter, IServerFilter
{
    private const string Param = "CorrelationId";
    private const string ScopeKey = "tailorly.logscope";

    public void OnCreating(CreatingContext context)
    {
        if (CorrelationIdMiddleware.Current is { } id)
            context.SetJobParameter(Param, id);
    }

    public void OnCreated(CreatedContext context) { }

    public void OnPerforming(PerformingContext context)
    {
        var id = context.GetJobParameter<string>(Param) ?? context.BackgroundJob.Id;
        CorrelationIdMiddleware.Current = id;
        context.Items[ScopeKey] = LogContext.PushProperty(CorrelationIdMiddleware.LogProperty, id);
    }

    public void OnPerformed(PerformedContext context)
    {
        if (context.Items.TryGetValue(ScopeKey, out var scope) && scope is IDisposable d)
            d.Dispose();
        CorrelationIdMiddleware.Current = null;
    }
}
