using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Tailorly.Tests.Infrastructure;

/// <summary>
/// Boots the real app (same Program, same pipeline) against a given database. Requests use
/// <c>https://localhost</c> so Secure/<c>__Host-</c> cookies behave exactly as in production.
/// </summary>
public sealed class TailorlyAppFactory(string connectionString, IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "tailorly-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Hangfire:ServerEnabled", "false");
        builder.UseSetting("Storage:RootPath", StorageRoot);
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        if (settings is not null)
            foreach (var (k, v) in settings)
                builder.UseSetting(k, v);
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { if (Directory.Exists(StorageRoot)) Directory.Delete(StorageRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
