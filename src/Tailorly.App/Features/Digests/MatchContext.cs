using Microsoft.EntityFrameworkCore;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Features.Resumes;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Matching;

namespace Tailorly.App.Features.Digests;

/// <summary>
/// Everything one user's run matches against: the active ready résumé (its version keys the scores), the effective
/// profile (user edits over the parsed one), skills (profile plus confirmed) and preferences.
/// </summary>
public sealed record MatchContext(
    Guid UserId,
    int ResumeVersion,
    ParsedProfile Profile,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> ConfirmedSkills,
    UserPreferences Preferences,
    MatchPreferences Match)
{
    /// <summary>The caller's preferences: the saved row, or the defaults if never saved. Reads through the user filter.</summary>
    public static async Task<UserPreferences> PreferencesAsync(AppDbContext db, Guid userId, CancellationToken ct) =>
        await db.UserPreferences.AsNoTracking().FirstOrDefaultAsync(ct) ?? new UserPreferences { UserId = userId };

    public static MatchPreferences ToMatch(UserPreferences p) => new()
    {
        SearchScope = p.SearchScope,
        TargetRoles = p.TargetRoles,
        Locations = p.Locations,
        WorkModes = p.WorkModes,
        JobTypes = p.JobTypes,
        HiringModels = p.HiringModels,
        ExcludedCompanies = p.ExcludedCompanies,
        ExcludedKeywords = p.ExcludedKeywords,
        SalaryMin = p.SalaryMin,
        SalaryMax = p.SalaryMax,
        SalaryCurrency = p.SalaryCurrency,
        MinMatchScore = p.MinMatchScore,
        IncludeUnspecifiedSalary = p.IncludeUnspecifiedSalary,
        ShowUnclearEligibility = p.ShowUnclearEligibility,
    };

    /// <summary>The caller's context, or null when there is no active résumé with a ready profile.</summary>
    public static async Task<MatchContext?> LoadAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        var resume = await db.Resumes.AsNoTracking()
            .Where(r => r.IsActive && r.ParseStatus == WorkStatus.Ready)
            .Select(r => new { r.Id, r.Version })
            .FirstOrDefaultAsync(ct);
        if (resume is null) return null;
        var profileRow = await db.ResumeProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.ResumeId == resume.Id, ct);
        if (profileRow is null) return null;

        var profile = ResumeEndpoints.ToDto(profileRow).Effective;
        var confirmed = await db.UserConfirmedSkills.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.Skill).ToListAsync(ct);
        var skills = profile.Skills.Select(s => s.Name).Concat(confirmed)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(300)
            .ToList();
        var prefs = await PreferencesAsync(db, userId, ct);
        return new MatchContext(userId, resume.Version, profile, skills, confirmed, prefs, ToMatch(prefs));
    }
}
