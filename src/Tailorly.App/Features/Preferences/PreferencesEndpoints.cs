using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tailorly.App.Api;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Jobs;

namespace Tailorly.App.Features.Preferences;

/// <summary>
/// Job preferences (PRD F3, ApiDesign §4.5). <c>GET</c> returns the defaults until the user saves; <c>PUT</c> is a full
/// replacement guarded by the <c>xmin</c> concurrency token (<c>409 CONCURRENT_UPDATE</c> when stale).
/// </summary>
public static class PreferencesEndpoints
{
    public const int MaxItems = 25;
    public const int MaxText = 100;
    public const long MaxSalary = 10_000_000_000;

    private static readonly string[] HiringModels = ["employee", "contractor"];
    private static readonly string[] Currencies = ["INR", "USD"];

    public static void MapPreferencesEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/preferences").WithTags("Preferences").RequireAuthorization(AuthPolicies.User);
        g.MapGet("", Get).WithName("GetPreferences");
        g.MapPut("", Update).WithName("UpdatePreferences");
    }

    private static async Task<Ok<PreferencesDto>> Get(AppDbContext db, ICurrentUser current, CancellationToken ct)
    {
        var p = await db.UserPreferences.AsNoTracking().FirstOrDefaultAsync(ct);
        return TypedResults.Ok(PreferencesDto.From(p ?? new UserPreferences { UserId = current.UserId }, saved: p is not null));
    }

    private static async Task<Ok<PreferencesDto>> Update(UpdatePreferencesRequest body, AppDbContext db, ICurrentUser current,
        TimeProvider clock, CancellationToken ct)
    {
        var errors = new FieldErrors();
        var scope = OneOf(errors, "searchScope", body.SearchScope, SearchScopes.All);
        var roles = TextList(errors, "targetRoles", body.TargetRoles);
        var locations = TextList(errors, "locations", body.Locations);
        var workModes = EnumList(errors, "workModes", body.WorkModes, WorkModes.All);
        var jobTypes = EnumList(errors, "jobTypes", body.JobTypes, EmploymentTypes.All);
        var hiring = EnumList(errors, "hiringModels", body.HiringModels, HiringModels);
        var currency = OneOf(errors, "salaryCurrency", body.SalaryCurrency, Currencies);
        var experience = errors.Text("experienceLevel", body.ExperienceLevel, MaxText, required: false);
        var overlap = errors.Text("timezoneOverlap", body.TimezoneOverlap, MaxText, required: false);
        var excludedCompanies = TextList(errors, "excludedCompanies", body.ExcludedCompanies);
        var excludedKeywords = TextList(errors, "excludedKeywords", body.ExcludedKeywords);

        foreach (var (field, value) in new[] { ("salaryMin", body.SalaryMin), ("salaryMax", body.SalaryMax) })
            if (value is < 0 or > MaxSalary) errors.Add(field, "Enter an annual amount between 0 and 10,000,000,000.");
        if (body.SalaryMin is { } min && body.SalaryMax is { } max && max < min)
            errors.Add("salaryMax", "Maximum must be at least the minimum.");
        if (body.MinMatchScore is not (>= 1 and <= 10)) errors.Add("minMatchScore", "Choose a score from 1 to 10.");
        if (!TimeOnly.TryParseExact(body.ScheduleLocalTime ?? "", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            errors.Add("scheduleLocalTime", "Use a 24-hour time such as 18:00.");
        foreach (var (field, value) in new[]
                 {
                     ("includeUnspecifiedSalary", body.IncludeUnspecifiedSalary), ("showUnclearEligibility", body.ShowUnclearEligibility),
                     ("scheduleEnabled", body.ScheduleEnabled), ("digestWhenEmpty", body.DigestWhenEmpty),
                 })
            if (value is null) errors.Add(field, "This field is required.");
        if (string.IsNullOrWhiteSpace(body.ConcurrencyToken)
            || !uint.TryParse(body.ConcurrencyToken, NumberStyles.None, CultureInfo.InvariantCulture, out var token))
        {
            errors.Add("concurrencyToken", "Reload the page and try again.");
            token = 0;
        }
        errors.ThrowIfAny();

        var p = await db.UserPreferences.FirstOrDefaultAsync(ct);
        var isNew = p is null;
        if (isNew)
        {
            if (body.ConcurrencyToken != PreferencesDto.NewToken) throw Stale();
            p = new UserPreferences { UserId = current.UserId };
            db.UserPreferences.Add(p);
        }
        else
        {
            if (body.ConcurrencyToken == PreferencesDto.NewToken) throw Stale();
            db.Entry(p!).Property(x => x.Version).OriginalValue = token;
        }

        p!.SearchScope = scope!;
        p.TargetRoles = roles;
        p.Locations = locations;
        p.WorkModes = workModes;
        p.JobTypes = jobTypes;
        p.HiringModels = hiring;
        p.SalaryMin = body.SalaryMin;
        p.SalaryMax = body.SalaryMax;
        p.SalaryCurrency = currency!;
        p.ExperienceLevel = experience;
        p.MinMatchScore = (short)body.MinMatchScore!.Value;
        p.IncludeUnspecifiedSalary = body.IncludeUnspecifiedSalary!.Value;
        p.ShowUnclearEligibility = body.ShowUnclearEligibility!.Value;
        p.TimezoneOverlap = overlap;
        p.ExcludedCompanies = excludedCompanies;
        p.ExcludedKeywords = excludedKeywords;
        p.ScheduleEnabled = body.ScheduleEnabled!.Value;
        p.ScheduleLocalTime = time;
        p.DigestWhenEmpty = body.DigestWhenEmpty!.Value;
        p.UpdatedAt = clock.GetUtcNow();

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Stale();
        }
        catch (DbUpdateException ex) when (isNew && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw Stale(); // saved from another tab in the meantime
        }
        return TypedResults.Ok(PreferencesDto.From(p, saved: true));
    }

    private static ApiException Stale() => new(StatusCodes.Status409Conflict, ErrorCodes.ConcurrentUpdate,
        "Preferences changed elsewhere", "Your preferences were changed in another tab. Reload them and try again.");

    private static string? OneOf(FieldErrors errors, string field, string? value, string[] allowed)
    {
        if (value is not null && allowed.Contains(value, StringComparer.Ordinal)) return value;
        errors.Add(field, $"Choose one of: {string.Join(", ", allowed)}.");
        return null;
    }

    /// <summary>Trimmed, non-empty, de-duplicated (case-insensitive), at most 25 items of at most 100 characters.</summary>
    private static List<string> TextList(FieldErrors errors, string field, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            errors.Add(field, "This field is required.");
            return [];
        }
        var list = values.Select(v => v?.Trim() ?? "").Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count > MaxItems) errors.Add(field, $"Add at most {MaxItems} items.");
        if (list.Any(v => v.Length > MaxText)) errors.Add(field, $"Each item must be {MaxText} characters or fewer.");
        return list;
    }

    private static List<string> EnumList(FieldErrors errors, string field, IReadOnlyList<string>? values, string[] allowed)
    {
        var list = values?.Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (list.Count == 0) errors.Add(field, "Choose at least one option.");
        else if (list.Any(v => !allowed.Contains(v, StringComparer.Ordinal))) errors.Add(field, $"Allowed values: {string.Join(", ", allowed)}.");
        return list;
    }
}
