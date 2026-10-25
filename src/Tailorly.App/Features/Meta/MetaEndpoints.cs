using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Infrastructure.Data;

namespace Tailorly.App.Features.Meta;

public sealed record MetaLimits(int ResumeMaxMb, int WatchlistMax);

public sealed record MetaResponse(
    string Version,
    string ConsentVersion,
    bool RequireInviteCode,
    IReadOnlyList<string> Tones,
    MetaLimits Limits);

/// <summary><c>GET /api/v1/meta</c> (ApiDesign §4.1): lets the SPA configure itself.</summary>
public static class MetaEndpoints
{
    private static readonly string Version =
        typeof(MetaEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    public static void MapMetaEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/meta", GetMeta)
            .WithName("GetMeta")
            .WithTags("Meta")
            .AllowAnonymous();
    }

    private static async Task<MetaResponse> GetMeta(AppDbContext db, IConfiguration config, CancellationToken ct)
    {
        var flag = await db.FeatureFlags.AsNoTracking()
            .Where(f => f.Key == FeatureFlag.SignupRequireInviteCode)
            .Select(f => f.Value)
            .FirstOrDefaultAsync(ct);

        return new MetaResponse(
            Version,
            config["Consent:Version"] ?? "1",
            RequireInviteCode: string.Equals(flag?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            Tones: ["concise", "confident"],
            Limits: new MetaLimits(ResumeMaxMb: 5, WatchlistMax: 25));
    }
}
