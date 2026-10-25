using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Tailorly.App.Infrastructure.Ai;
using Tailorly.Core.Ports;

namespace Tailorly.Tests.Infrastructure;

/// <summary>
/// Boots the real app (same Program, same pipeline) against a given database. Requests use
/// <c>https://localhost</c> so Secure/<c>__Host-</c> cookies behave exactly as in production. Background jobs run
/// inline on demand (<see cref="Jobs"/>), emails are captured (<see cref="Emails"/>), logs too (<see cref="Logs"/>), and
/// the AI provider is the fake (<see cref="Ai"/>).
/// </summary>
public sealed class TailorlyAppFactory(string connectionString, IReadOnlyDictionary<string, string?>? settings = null,
    bool relaxedRateLimits = true)
    : WebApplicationFactory<Program>
{
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "tailorly-tests", Guid.NewGuid().ToString("N"));
    public string DeletionLogPath => Path.Combine(StorageRoot, "..", Path.GetFileName(StorageRoot) + "-deletions.log");

    public CapturingEmailSender Emails { get; } = new();
    public CapturingLogSink Logs { get; } = new();

    public InlineJobClient Jobs => (InlineJobClient)Services.GetRequiredService<IBackgroundJobClient>();
    public FakeModelProvider Ai => Services.GetRequiredService<FakeModelProvider>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Hangfire:ServerEnabled", "false");
        builder.UseSetting("Storage:RootPath", StorageRoot);
        builder.UseSetting("DeletionLog:Path", DeletionLogPath);
        builder.UseSetting("App:PublicBaseUrl", "https://tailorly.test");
        builder.UseSetting("Security:IpHashKey", "test-ip-hash-key");
        builder.UseSetting("Ai:Provider", "Fake");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        if (relaxedRateLimits)
        {
            // The shared factory registers many users from one (unknown) IP; rate-limit tests use their own factory.
            foreach (var bucket in new[] { "auth-login", "auth-register", "auth-mail", "ai-start", "api-default", "download",
                         "auth-register-email", "auth-mail-email", "auth-resend-user" })
                builder.UseSetting($"RateLimits:{bucket}:PermitLimit", "100000");
        }
        if (settings is not null)
            foreach (var (k, v) in settings)
                builder.UseSetting(k, v);

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IBackgroundJobClient>(sp => new InlineJobClient(sp));
            services.AddSingleton<IEmailSender>(Emails);
            services.AddSingleton<ILogEventSink>(Logs);
        });
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>A client that fetches the CSRF cookie first and echoes it on unsafe requests, like the SPA.</summary>
    public async Task<ApiClient> CreateApiClientAsync()
    {
        var api = new ApiClient(CreateHttpsClient());
        await api.RefreshCsrfAsync();
        return api;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { if (Directory.Exists(StorageRoot)) Directory.Delete(StorageRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
        try { if (File.Exists(DeletionLogPath)) File.Delete(DeletionLogPath); }
        catch (IOException) { /* best effort */ }
    }
}
