using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Jobs;
using Tailorly.Core.Time;

namespace Tailorly.App.Features.Admin;

/// <summary>
/// The M2 coverage report (PRD §7.1b "Coverage check", SDD §8.0, ApiDesign §4.11): per day (Asia/Kolkata, by first-seen
/// date) × source × eligibility, and per sample role, the unique listings that are open to India. Duplicates suppressed
/// by dedupe are excluded. Eligibility comes from <see cref="EligibilityRules"/> (structured source fields, then phrases)
/// because enrichment arrives in M3; the M3 enrichment's value should replace it when present.
/// </summary>
public sealed class CoverageReport(AppDbContext db, TimeProvider clock)
{
    public static readonly string[] DefaultRoles = ["backend", "frontend", "data", "devops", "mobile"];
    public const int MaxDays = 31;

    public async Task<CoverageReportDto> BuildAsync(DateOnly? from, DateOnly? to, IReadOnlyList<string> roles, CancellationToken ct)
    {
        var india = TimeZones.RequireIana(TimeZones.India);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), india).Date);
        var end = to ?? today;
        var start = from ?? end.AddDays(-6);
        if (start > end) (start, end) = (end, start);
        if (end.DayNumber - start.DayNumber + 1 > MaxDays) start = end.AddDays(-(MaxDays - 1));

        var fromUtc = LocalMidnightUtc(start, india);
        var toUtc = LocalMidnightUtc(end.AddDays(1), india);
        var matchers = roles.Select(r => (Role: r, Regex: RoleMatcher.For(r))).ToList();

        var days = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(i => start.AddDays(i))
            .ToDictionary(d => d, _ => new DayAcc());
        var rows = db.Jobs.AsNoTracking()
            .Where(j => j.DuplicateOfJobId == null && j.CreatedAt >= fromUtc && j.CreatedAt < toUtc)
            .Select(j => new { j.SourceKey, j.CreatedAt, j.Title, j.LocationText, j.SourceMeta, j.DescriptionText })
            .AsAsyncEnumerable();

        await foreach (var r in rows.WithCancellation(ct))
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.CreatedAt, india).Date);
            if (!days.TryGetValue(day, out var acc)) continue;
            var eligibility = EligibilityRules.Classify(Countries(r.SourceMeta), r.LocationText, r.Title, r.DescriptionText).Eligibility;
            acc.Add(r.SourceKey, eligibility);
            if (!Eligibility.OpenToIndia(eligibility)) continue;
            foreach (var (role, regex) in matchers)
                if (regex.IsMatch(r.Title)) acc.Roles[role] = acc.Roles.GetValueOrDefault(role) + 1;
        }

        var dayDtos = days.OrderBy(d => d.Key).Select(d => d.Value.ToDto(d.Key, roles)).ToList();
        var all = new DayAcc();
        foreach (var d in days.Values) all.Merge(d);
        var totals = all.ToDto(end, roles);
        return new CoverageReportDto(start, end, roles, "rules", totals.Total, totals.OpenToIndia,
            Math.Round(totals.OpenToIndia / (double)dayDtos.Count, 1), totals.Sources, totals.Roles, dayDtos);
    }

    /// <summary>Structured country restrictions stored by sources that have them (<c>source_meta.locationRestriction</c>).</summary>
    private static List<string>? Countries(string? sourceMeta)
    {
        if (string.IsNullOrEmpty(sourceMeta)) return null;
        using var doc = JsonDocument.Parse(sourceMeta);
        return doc.RootElement.ValueKind == JsonValueKind.Object
               && doc.RootElement.TryGetProperty("locationRestriction", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : null;
    }

    private static DateTimeOffset LocalMidnightUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Parses the <c>roles</c> query: comma-separated keywords, trimmed, at most 10 of at most 40 characters.</summary>
    public static IReadOnlyList<string> ParseRoles(string? roles)
    {
        if (string.IsNullOrWhiteSpace(roles)) return DefaultRoles;
        var list = roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => r.Length is > 0 and <= 40).Select(r => r.ToLowerInvariant()).Distinct().Take(10).ToList();
        return list.Count == 0 ? DefaultRoles : list;
    }

    private sealed class DayAcc
    {
        public Dictionary<string, int[]> Sources { get; } = new(StringComparer.Ordinal); // [worldwide, india, restricted, unclear]
        public Dictionary<string, int> Roles { get; } = new(StringComparer.Ordinal);

        public void Add(string source, string eligibility)
        {
            if (!Sources.TryGetValue(source, out var c)) Sources[source] = c = new int[4];
            c[eligibility switch
            {
                Eligibility.Worldwide => 0,
                Eligibility.IndiaEligible => 1,
                Eligibility.Restricted => 2,
                _ => 3,
            }]++;
        }

        public void Merge(DayAcc other)
        {
            foreach (var (k, v) in other.Sources)
            {
                if (!Sources.TryGetValue(k, out var c)) Sources[k] = c = new int[4];
                for (var i = 0; i < 4; i++) c[i] += v[i];
            }
            foreach (var (k, v) in other.Roles) Roles[k] = Roles.GetValueOrDefault(k) + v;
        }

        public CoverageDayDto ToDto(DateOnly date, IReadOnlyList<string> roles)
        {
            var sources = Sources.OrderBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => new CoverageSourceDto(s.Key, s.Value.Sum(), s.Value[0], s.Value[1], s.Value[2], s.Value[3])).ToList();
            return new CoverageDayDto(date, sources.Sum(s => s.Total), sources.Sum(s => s.OpenToIndia), sources,
                roles.ToDictionary(r => r, r => Roles.GetValueOrDefault(r)));
        }
    }
}

/// <summary>Title matchers for coverage roles. The five sample roles have built-in synonyms; any other keyword is a word match.</summary>
public static class RoleMatcher
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["backend"] = @"back[\s-]?end|server[\s-]side|\bapi (engineer|developer)\b",
        ["frontend"] = @"front[\s-]?end|\bui (engineer|developer)\b|\breact (engineer|developer)\b",
        ["data"] = @"\bdata\b|machine learning|\bml\b|\bai engineer\b|analytics",
        ["devops"] = @"dev\s?ops|\bsre\b|site reliability|platform engineer|infrastructure|\bcloud engineer\b",
        ["mobile"] = @"mobile|\bios\b|android|react native|flutter",
        ["fullstack"] = @"full[\s-]?stack",
    };

    public static Regex For(string role) => new(
        Known.TryGetValue(role, out var pattern) ? pattern : @"\b" + Regex.Escape(role) + @"\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
