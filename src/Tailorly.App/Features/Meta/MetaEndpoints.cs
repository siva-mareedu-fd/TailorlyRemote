using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tailorly.App.Features.Accounts;
using Tailorly.App.Features.Tailoring;
using Tailorly.App.Infrastructure.Data;

namespace Tailorly.App.Features.Meta;

public sealed record MetaLimits(int ResumeMaxMb, int WatchlistMax);

/// <param name="Tones">Cover-letter tones (configuration, PRD D8).</param>
/// <param name="ToneWordLimits">Word limit per tone (PRD F9: concise 180, confident 250).</param>
/// <param name="PdfExport">The <c>pdf_export.enabled</c> flag: whether documents can be downloaded as PDF.</param>
public sealed record MetaResponse(
    string Version,
    string ConsentVersion,
    bool RequireInviteCode,
    IReadOnlyList<string> Tones,
    MetaLimits Limits,
    IReadOnlyDictionary<string, int> ToneWordLimits,
    bool PdfExport);

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

    private static async Task<MetaResponse> GetMeta(AppDbContext db, IConfiguration config, IOptions<CoverLetterOptions> letters,
        CancellationToken ct)
    {
        var flag = await db.FeatureFlags.AsNoTracking()
            .Where(f => f.Key == FeatureFlag.SignupRequireInviteCode)
            .Select(f => f.Value)
            .FirstOrDefaultAsync(ct);
        var tones = letters.Value.EffectiveTones;

        return new MetaResponse(
            Version,
            config["Consent:Version"] ?? "1",
            RequireInviteCode: string.Equals(flag?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            Tones: [.. tones.Keys],
            Limits: new MetaLimits(ResumeMaxMb: 5, WatchlistMax: 25),
            ToneWordLimits: tones,
            PdfExport: await FeatureFlags.IsOnAsync(db, FeatureFlag.PdfExportEnabled, ct));
    }
}
