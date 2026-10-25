using Serilog;
using Tailorly.App.Infrastructure.Data;
using Tailorly.App.Startup;
using Tailorly.Core.Time;

// Tailorly.App: API + Hangfire server in one process (ADR-001).
//   Tailorly.App            run the web host
//   Tailorly.App --migrate  apply EF migrations + Hangfire schema + idempotent seed, then exit
//   Tailorly.App --seed     run the idempotent seed only, then exit

// Minimal logger for startup failures; replaced by the configured logger once the host is built.
Log.Logger = new LoggerConfiguration()
    .Enrich.With<Tailorly.App.Infrastructure.Logging.PiiScrubbingEnricher>()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
    .CreateLogger();

try
{
    // Startup self-test (SDD §13.2): fail fast if the runtime image lacks time-zone data.
    TimeZones.RequireIana(TimeZones.India);

    var builder = WebApplication.CreateBuilder(args);
    builder.AddTailorly();
    var app = builder.Build();

    if (args.Contains("--migrate") || args.Contains("--seed"))
    {
        await using var scope = app.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
        if (args.Contains("--migrate"))
            await runner.MigrateAsync(CancellationToken.None);
        else
            await runner.SeedAsync(CancellationToken.None);
        return 0;
    }

    app.UseTailorly();
    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Tailorly.App terminated during startup");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
