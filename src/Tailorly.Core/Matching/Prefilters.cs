using Tailorly.Core.Jobs;

namespace Tailorly.Core.Matching;

/// <summary>
/// The raw-field prefilter (PRD §7.5 step 1, SDD §8.5): from title, company, location text, structured source fields and
/// the start of the description, keep the jobs that plausibly fit the user's roles/skills, scope, work modes and job
/// types, and are not excluded. Runs before any model call, so it decides which jobs are worth enriching.
/// </summary>
public static class RawPrefilter
{
    public const string ExcludedCompany = "excluded_company";
    public const string ExcludedKeyword = "excluded_keyword";
    public const string WorkMode = "work_mode";
    public const string JobType = "job_type";
    public const string Restricted = "restricted";
    public const string OutOfScope = "out_of_scope";
    public const string NoMatch = "no_role_or_skill_match";
    public const string Kept = "kept";

    /// <param name="skills">The user's profile skills plus confirmed skills.</param>
    public static PrefilterVerdict Apply(RawCandidate job, MatchPreferences prefs, IReadOnlyList<string> skills)
    {
        if (IsExcludedCompany(job.CompanyName, prefs.ExcludedCompanies)) return PrefilterVerdict.Drop(ExcludedCompany);
        if (TermMatcher.ContainsAny(job.Title, prefs.ExcludedKeywords) || TermMatcher.ContainsAny(job.DescriptionHead, prefs.ExcludedKeywords))
            return PrefilterVerdict.Drop(ExcludedKeyword);
        if (job.WorkMode is { } mode && !prefs.WorkModes.Contains(mode, StringComparer.Ordinal)) return PrefilterVerdict.Drop(WorkMode);
        if (job.EmploymentType is { } type && !prefs.JobTypes.Contains(type, StringComparer.Ordinal)) return PrefilterVerdict.Drop(JobType);

        var hint = EligibilityRules.Classify(job.Countries, job.LocationText, job.Title, job.DescriptionHead);
        if (hint.Eligibility == Eligibility.Restricted) return PrefilterVerdict.Drop(Restricted);
        var indiaLocated = EligibilityRules.ListsIndia(job.Countries) || EligibilityRules.NamesIndia(job.LocationText)
                           || EligibilityRules.NamesIndia(job.Title);
        if (!InScope(prefs.SearchScope, job.WorkMode, indiaLocated)) return PrefilterVerdict.Drop(OutOfScope);

        var role = TermMatcher.RoleScore(job.Title, prefs.TargetRoles);
        var titleHits = TermMatcher.SkillHits(job.Title, skills, allowShort: true);
        var headHits = TermMatcher.SkillHits(job.DescriptionHead, skills, allowShort: false);
        var relevance = 2 * role + 0.6 * Math.Min(titleHits, 3) + 0.3 * Math.Min(headHits, 4);

        // Nothing to match on (no roles, no skills): keep everything in scope, equally relevant.
        if (prefs.TargetRoles.Count == 0 && skills.Count == 0) return new PrefilterVerdict(true, 0.5, Kept);
        return role >= 0.5 || titleHits >= 1 || headHits >= 2
            ? new PrefilterVerdict(true, relevance, Kept)
            : PrefilterVerdict.Drop(NoMatch);
    }

    /// <summary>
    /// Scope on raw fields: india_only needs an India location; global_remote_only needs a remote (or unknown) work mode;
    /// India + global remote takes remote jobs and India-located jobs.
    /// </summary>
    internal static bool InScope(string scope, string? workMode, bool indiaLocated)
    {
        var remoteOrUnknown = workMode is null or WorkModes.Remote;
        return scope switch
        {
            Scope.IndiaOnly => indiaLocated,
            Scope.GlobalRemoteOnly => remoteOrUnknown,
            _ => remoteOrUnknown || indiaLocated,
        };
    }

    internal static bool IsExcludedCompany(string company, IReadOnlyList<string> excluded)
    {
        if (excluded.Count == 0) return false;
        var name = NameNormalizer.Company(company);
        if (name.Length == 0) return false;
        return excluded.Select(NameNormalizer.Company).Where(e => e.Length > 0)
            .Any(e => name == e || (" " + name + " ").Contains(" " + e + " ", StringComparison.Ordinal));
    }
}

/// <summary>
/// The enriched prefilter (PRD §7.5 step 1, after enrichment): eligibility against the search scope, work mode, job
/// type, hiring model and (for jobs located in India) the user's cities. Restricted jobs never pass.
/// </summary>
public static class EnrichedPrefilter
{
    public const string Restricted = "restricted";
    public const string OutOfScope = "out_of_scope";
    public const string WorkMode = "work_mode";
    public const string JobType = "job_type";
    public const string HiringModel = "hiring_model";
    public const string Location = "location";
    public const string Kept = "kept";

    /// <summary>Common alternative spellings of Indian cities users type in preferences.</summary>
    private static readonly Dictionary<string, string[]> CityAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bengaluru"] = ["bangalore"], ["bangalore"] = ["bengaluru"],
        ["gurugram"] = ["gurgaon"], ["gurgaon"] = ["gurugram"],
        ["mumbai"] = ["bombay"], ["chennai"] = ["madras"], ["kolkata"] = ["calcutta"],
        ["delhi"] = ["new delhi", "ncr"], ["new delhi"] = ["delhi", "ncr"],
        ["mysuru"] = ["mysore"], ["mysore"] = ["mysuru"],
        ["thiruvananthapuram"] = ["trivandrum"], ["trivandrum"] = ["thiruvananthapuram"],
    };

    public static string Check(EnrichedFacts job, MatchPreferences prefs)
    {
        if (job.Eligibility == Eligibility.Restricted) return Restricted;
        var remoteOrUnknown = job.WorkMode is null or WorkModes.Remote;
        var inScope = prefs.SearchScope switch
        {
            Scope.IndiaOnly => job.IndiaLocated,
            Scope.GlobalRemoteOnly => remoteOrUnknown,
            _ => remoteOrUnknown || job.IndiaLocated,
        };
        if (!inScope) return OutOfScope;
        if (job.WorkMode is { } mode && !prefs.WorkModes.Contains(mode, StringComparer.Ordinal)) return WorkMode;
        if (job.EmploymentType is { } type && !prefs.JobTypes.Contains(type, StringComparer.Ordinal)) return JobType;
        if (job.HiringModel is "employee" or "contractor" && !prefs.HiringModels.Contains(job.HiringModel, StringComparer.Ordinal))
            return HiringModel;
        if (!remoteOrUnknown && job.IndiaLocated && prefs.Locations.Count > 0 && !InUserCity(job.LocationText, prefs.Locations))
            return Location;
        return Kept;
    }

    internal static bool InUserCity(string? locationText, IReadOnlyList<string> cities)
    {
        if (string.IsNullOrWhiteSpace(locationText)) return false;
        var names = cities.SelectMany(c => CityAliases.TryGetValue(c.Trim(), out var a) ? a.Append(c.Trim()) : [c.Trim()]).ToList();
        return TermMatcher.ContainsAny(locationText, names);
    }
}
