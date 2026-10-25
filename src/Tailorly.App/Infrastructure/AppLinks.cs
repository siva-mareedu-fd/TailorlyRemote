using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Tailorly.App.Infrastructure;

public sealed class AppOptions
{
    /// <summary>Public origin of the SPA (e.g. <c>https://tailorly.example</c>); used only to build links in emails.</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost";
}

/// <summary>Builds absolute SPA links for emails from fixed paths. Never from request headers (host-header injection).</summary>
public sealed class AppLinks(IOptions<AppOptions> options)
{
    public string Spa(string path, IDictionary<string, string?> query) =>
        QueryHelpers.AddQueryString(options.Value.PublicBaseUrl.TrimEnd('/') + path, query);
}
