using Tailorly.Core.Matching;

namespace Tailorly.Core.Ats;

/// <summary>
/// <c>title_seniority</c> (PRD §8.2: rules + model label). The rules compare the candidate's level (from the most recent
/// title, else from years) with the job's seniority, and the candidate's years with the job's range; the component is
/// the mean of the rule part and the model's label, using whichever exist.
/// </summary>
public static class TitleSeniority
{
    /// <summary>Levels on one ladder; lead, staff and manager share a rung (different tracks, similar seniority).</summary>
    private static readonly Dictionary<string, int> JobLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["intern"] = 0, ["junior"] = 1, ["mid"] = 2, ["senior"] = 3, ["lead"] = 4, ["staff"] = 4, ["manager"] = 4, ["principal"] = 5,
    };

    /// <summary>Title words that name a level, checked in this order (a "Senior Staff" title is staff).</summary>
    private static readonly (string Word, int Level)[] TitleWords =
    [
        ("intern", 0), ("trainee", 0), ("apprentice", 0),
        ("principal", 5), ("distinguished", 5), ("architect", 5),
        ("staff", 4), ("lead", 4), ("manager", 4), ("head", 4), ("director", 4),
        ("senior", 3), ("sr", 3),
        ("junior", 1), ("jr", 1), ("graduate", 1), ("associate", 1),
    ];

    public static double? Compute(SeniorityInput input)
    {
        var rules = Mean(LevelScore(input), YearsScore(input));
        var label = input.ModelLabel is { } l && !double.IsNaN(l) ? Math.Clamp(l, 0, 1) : (double?)null;
        return Mean(rules, label);
    }

    /// <summary>1.0 on the same rung, 0.7 one apart, 0.4 two apart, 0.1 further; null if the job's seniority is unknown.</summary>
    public static double? LevelScore(SeniorityInput input)
    {
        if (input.JobSeniority is not { } s || !JobLevels.TryGetValue(s.Trim(), out var job)) return null;
        var distance = Math.Abs(CandidateLevel(input.RecentTitles, input.CandidateYears) - job);
        return distance switch { 0 => 1.0, 1 => 0.7, 2 => 0.4, _ => 0.1 };
    }

    /// <summary>
    /// 1.0 inside the range; short of the minimum, the share of it the candidate has; well over the maximum (more than
    /// 3 years) 0.8. Null if the job states no years or the profile has none.
    /// </summary>
    public static double? YearsScore(SeniorityInput input)
    {
        if (input.CandidateYears is not { } years || double.IsNaN(years)) return null;
        if (input.JobYearsMin is { } min && min > 0 && years < min) return Math.Clamp(years / min, 0, 1);
        if (input.JobYearsMax is { } max && years > max + 3) return 0.8;
        return input.JobYearsMin is null && input.JobYearsMax is null ? null : 1.0;
    }

    /// <summary>The level named in the most recent title; without a level word, from years (&lt;2 junior, &lt;5 mid, &lt;8 senior, else lead).</summary>
    public static int CandidateLevel(IReadOnlyList<string> recentTitles, double? years)
    {
        var words = TermMatcher.Words(recentTitles.FirstOrDefault()).ToHashSet(StringComparer.Ordinal);
        foreach (var (word, level) in TitleWords)
            if (words.Contains(word)) return level;
        return years switch
        {
            null => 2,
            < 2 => 1,
            < 5 => 2,
            < 8 => 3,
            _ => 4,
        };
    }

    private static double? Mean(params double?[] values)
    {
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : present.Average();
    }
}
