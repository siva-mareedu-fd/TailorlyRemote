using Tailorly.App.Infrastructure.Data;

namespace Tailorly.App.Features.Preferences;

// HTTP contract of ApiDesign §4.5.

/// <param name="SearchScope">india_only | india_plus_global_remote | global_remote_only.</param>
/// <param name="ScheduleLocalTime"><c>HH:mm</c> in the user's time zone.</param>
/// <param name="ConcurrencyToken">Send back unchanged with <c>PUT</c>; a stale token is <c>409 CONCURRENT_UPDATE</c>.</param>
public sealed record PreferencesDto(
    string SearchScope,
    IReadOnlyList<string> TargetRoles,
    IReadOnlyList<string> Locations,
    IReadOnlyList<string> WorkModes,
    IReadOnlyList<string> JobTypes,
    IReadOnlyList<string> HiringModels,
    long? SalaryMin,
    long? SalaryMax,
    string SalaryCurrency,
    string? ExperienceLevel,
    int MinMatchScore,
    bool IncludeUnspecifiedSalary,
    bool ShowUnclearEligibility,
    string? TimezoneOverlap,
    IReadOnlyList<string> ExcludedCompanies,
    IReadOnlyList<string> ExcludedKeywords,
    bool ScheduleEnabled,
    string ScheduleLocalTime,
    bool DigestWhenEmpty,
    string ConcurrencyToken)
{
    /// <summary>Token of preferences that were never saved (the defaults).</summary>
    public const string NewToken = "0";

    public static PreferencesDto From(UserPreferences p, bool saved) => new(
        p.SearchScope, p.TargetRoles, p.Locations, p.WorkModes, p.JobTypes, p.HiringModels, p.SalaryMin, p.SalaryMax,
        p.SalaryCurrency, p.ExperienceLevel, p.MinMatchScore, p.IncludeUnspecifiedSalary, p.ShowUnclearEligibility,
        p.TimezoneOverlap, p.ExcludedCompanies, p.ExcludedKeywords, p.ScheduleEnabled,
        p.ScheduleLocalTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), p.DigestWhenEmpty,
        saved ? p.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) : NewToken);
}

/// <summary>Full replacement of the preferences. Every field is required except the nullable ones.</summary>
public sealed record UpdatePreferencesRequest(
    string? SearchScope,
    IReadOnlyList<string>? TargetRoles,
    IReadOnlyList<string>? Locations,
    IReadOnlyList<string>? WorkModes,
    IReadOnlyList<string>? JobTypes,
    IReadOnlyList<string>? HiringModels,
    long? SalaryMin,
    long? SalaryMax,
    string? SalaryCurrency,
    string? ExperienceLevel,
    int? MinMatchScore,
    bool? IncludeUnspecifiedSalary,
    bool? ShowUnclearEligibility,
    string? TimezoneOverlap,
    IReadOnlyList<string>? ExcludedCompanies,
    IReadOnlyList<string>? ExcludedKeywords,
    bool? ScheduleEnabled,
    string? ScheduleLocalTime,
    bool? DigestWhenEmpty,
    string? ConcurrencyToken);

/// <param name="Id">The board link id (use it with <c>DELETE /watched-companies/{id}</c>).</param>
/// <param name="Status">resolved | not_found | blocked.</param>
/// <param name="Origin">How the board first became known: seed | user | discovered | guessed | admin.</param>
public sealed record WatchedCompanyDto(Guid Id, string Name, string Platform, string BoardToken, string Status, int? OpenJobsCount,
    string Origin);

/// <summary>Either <see cref="BoardUrl"/> or <see cref="Platform"/> + <see cref="BoardToken"/>.</summary>
public sealed record AddWatchedCompanyRequest(string? BoardUrl, string? Platform, string? BoardToken, string? Name);
