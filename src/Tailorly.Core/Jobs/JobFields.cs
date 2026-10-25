using System.Text.RegularExpressions;

namespace Tailorly.Core.Jobs;

public static class WorkModes
{
    public const string Remote = "remote";
    public const string Hybrid = "hybrid";
    public const string Onsite = "onsite";
    public static readonly string[] All = [Remote, Hybrid, Onsite];
}

public static class EmploymentTypes
{
    public const string FullTime = "full_time";
    public const string Contract = "contract";
    public const string PartTime = "part_time";
    public const string Internship = "internship";
    public static readonly string[] All = [FullTime, Contract, PartTime, Internship];
}

/// <summary>Maps each source's wording to the enum columns of <c>jobs</c> (SDD §8.2). Unknown wording maps to null.</summary>
public static partial class JobFields
{
    /// <summary>"Full Time", "FullTime", "Full-Time", "Contractor", "Temporary", "Intern"… → <see cref="EmploymentTypes"/>.</summary>
    public static string? EmploymentType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = Letters().Replace(raw.ToLowerInvariant(), "");
        if (s.Contains("intern", StringComparison.Ordinal) || s.Contains("trainee", StringComparison.Ordinal)) return EmploymentTypes.Internship;
        if (s.Contains("parttime", StringComparison.Ordinal)) return EmploymentTypes.PartTime;
        if (s.Contains("contract", StringComparison.Ordinal) || s.Contains("freelance", StringComparison.Ordinal)
            || s.Contains("temporary", StringComparison.Ordinal) || s is "temp") return EmploymentTypes.Contract;
        if (s.Contains("fulltime", StringComparison.Ordinal) || s.Contains("permanent", StringComparison.Ordinal)) return EmploymentTypes.FullTime;
        return null;
    }

    /// <summary><c>contractor</c> for contract/freelance wording, <c>employee</c> for full/part-time, else null.</summary>
    public static string? HiringModelHint(string? rawEmploymentType) => EmploymentType(rawEmploymentType) switch
    {
        EmploymentTypes.Contract => "contractor",
        EmploymentTypes.FullTime or EmploymentTypes.PartTime => "employee",
        _ => null,
    };

    /// <summary>
    /// Work mode from the source's workplace type, then the location text. Listings on remote-only boards (Himalayas,
    /// Remote OK, Jobicy, WWR) default to remote; Remote OK's flag is unverified (PRD §7.1), which eligibility handles.
    /// </summary>
    public static string? WorkMode(string? workplaceType, string? locationText, bool remoteOnlySource)
    {
        if (Classify(workplaceType) is { } fromType) return fromType;
        if (Classify(locationText) is { } fromText) return fromText;
        if (remoteOnlySource) return WorkModes.Remote;
        return string.IsNullOrWhiteSpace(locationText) ? null : WorkModes.Onsite;

        static string? Classify(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.ToLowerInvariant();
            if (t.Contains("hybrid", StringComparison.Ordinal)) return WorkModes.Hybrid;
            if (t.Contains("remote", StringComparison.Ordinal) || t.Contains("anywhere", StringComparison.Ordinal)
                || t.Contains("worldwide", StringComparison.Ordinal) || t.Contains("work from home", StringComparison.Ordinal)) return WorkModes.Remote;
            if (t.Contains("on-site", StringComparison.Ordinal) || t.Contains("onsite", StringComparison.Ordinal)
                || t.Contains("in office", StringComparison.Ordinal) || t.Contains("in-office", StringComparison.Ordinal)) return WorkModes.Onsite;
            return null;
        }
    }

    /// <summary>
    /// <c>remote-worldwide | remote-region:{x} | city:{y} | unknown</c> (DatabaseDesign §3.4). Structured country lists win;
    /// otherwise the location text is read.
    /// </summary>
    public static string LocationClass(string? workMode, IReadOnlyList<string>? countries, string? locationText)
    {
        var text = locationText?.Trim() ?? "";
        if (workMode == WorkModes.Remote)
        {
            if (countries is not null)
                return countries.Count == 0 ? "remote-worldwide" : "remote-region:" + Region(countries);
            if (Worldwide().IsMatch(text)) return "remote-worldwide";
            var rest = RemoteWord().Replace(text, " ").Trim(' ', ',', '-', '–', '(', ')', '/', '|');
            return rest.Length == 0 ? "unknown" : "remote-region:" + Slug(rest);
        }
        if (text.Length == 0) return "unknown";
        var city = text.Split([',', ';', '|', '/'], 2)[0];
        city = RemoteWord().Replace(city, " ").Trim(' ', '-', '–', '(', ')');
        return city.Length == 0 ? "unknown" : "city:" + Slug(city);
    }

    private static string Region(IReadOnlyList<string> countries)
    {
        var names = countries.Select(Slug).Where(s => s.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList();
        return names.Count <= 3 ? string.Join(',', names) : string.Join(',', names.Take(3)) + $",+{names.Count - 3}";
    }

    private static string Slug(string s)
    {
        var slug = NonSlug().Replace(s.ToLowerInvariant(), "-").Trim('-');
        return slug.Length <= 60 ? slug : slug[..60];
    }

    [GeneratedRegex(@"[^a-z]", RegexOptions.None, 1000)]
    private static partial Regex Letters();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.None, 1000)]
    private static partial Regex NonSlug();

    [GeneratedRegex(@"\b(anywhere|worldwide|global(ly)?|work from anywhere|any location)\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Worldwide();

    [GeneratedRegex(@"\b(fully |100% )?remote( first| only| friendly)?\b|\bhybrid\b|\bwork from home\b|\bwfh\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex RemoteWord();
}
