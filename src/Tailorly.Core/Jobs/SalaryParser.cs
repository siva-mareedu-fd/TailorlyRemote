using System.Globalization;
using System.Text.RegularExpressions;

namespace Tailorly.Core.Jobs;

/// <summary>Salary kinds stored in <c>jobs.salary_kind</c>.</summary>
public static class SalaryKinds
{
    public const string Stated = "stated";
    public const string Estimated = "estimated";
    public const string Unknown = "unknown";
}

/// <summary>A salary normalised to whole units per year in its own currency (DatabaseDesign §1 "Money").</summary>
/// <param name="PeriodOriginal">hour | day | week | fortnight | month | year, as published.</param>
public sealed record Salary(long? Min, long? Max, string? Currency, string? PeriodOriginal, string Kind)
{
    public static readonly Salary None = new(null, null, null, null, SalaryKinds.Unknown);
}

/// <summary>
/// Salary parsing (SDD §8.2): structured fields first, then a conservative regex over the description. Amounts are
/// annualised (hour × 2080, day × 260, week × 52, fortnight × 26, month × 12). Anything implausible is dropped rather than
/// guessed: a wrong salary would wrongly drop a job under the salary rule (PRD §7.5).
/// </summary>
public static partial class SalaryParser
{
    private const long MaxAnnual = 10_000_000_000;

    private static readonly Dictionary<string, int> PerYear = new(StringComparer.Ordinal)
    {
        ["hour"] = 2080, ["day"] = 260, ["week"] = 52, ["fortnight"] = 26, ["month"] = 12, ["year"] = 1,
    };

    /// <summary>Maps a published period word (hourly, per month, annual, yearly, LPA…) to the stored form, or null.</summary>
    public static string? NormalizePeriod(string? period)
    {
        if (string.IsNullOrWhiteSpace(period)) return null;
        var p = period.Trim().ToLowerInvariant();
        if (p.Contains("hour", StringComparison.Ordinal) || p is "hr" or "/hr" or "ph") return "hour";
        if (p.Contains("fortnight", StringComparison.Ordinal) || p.Contains("biweek", StringComparison.Ordinal)) return "fortnight";
        if (p.Contains("week", StringComparison.Ordinal) || p is "wk" or "/wk") return "week";
        if (p.Contains("day", StringComparison.Ordinal) || p.Contains("daily", StringComparison.Ordinal)) return "day";
        if (p.Contains("month", StringComparison.Ordinal) || p is "mo" or "/mo" or "pm") return "month";
        if (p.Contains("year", StringComparison.Ordinal) || p.Contains("annu", StringComparison.Ordinal) || p is "yr" or "/yr" or "pa" or "lpa" or "1 year")
            return "year";
        return null;
    }

    public static string? NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency)) return null;
        var c = currency.Trim();
        return c switch
        {
            "$" => "USD",
            "€" => "EUR",
            "£" => "GBP",
            "₹" => "INR",
            _ when c.Length == 3 && c.All(char.IsAsciiLetter) => c.ToUpperInvariant(),
            _ => null,
        };
    }

    /// <summary>Structured salary from a source. A missing period means annual; a missing currency means unknown salary.</summary>
    public static Salary FromStructured(decimal? min, decimal? max, string? currency, string? period, bool estimate)
    {
        var cur = NormalizeCurrency(currency);
        var per = period is null ? "year" : NormalizePeriod(period);
        if (cur is null || per is null) return Salary.None;
        var lo = Annualize(min, per);
        var hi = Annualize(max, per);
        if (lo is null && hi is null) return Salary.None;
        if (lo > hi) (lo, hi) = (hi, lo);
        return new Salary(lo, hi, cur, per, estimate ? SalaryKinds.Estimated : SalaryKinds.Stated);
    }

    /// <summary>Annual whole units, or null for a missing, non-positive or implausible amount.</summary>
    public static long? Annualize(decimal? amount, string period)
    {
        if (amount is not { } a || a <= 0 || !PerYear.TryGetValue(period, out var factor)) return null;
        var annual = decimal.Round(a * factor, 0, MidpointRounding.AwayFromZero);
        return annual is > 0 and <= MaxAnnual ? (long)annual : null;
    }

    /// <summary>
    /// Regex fallback for plain-text descriptions: a currency-marked amount or range (<c>$120,000 – $150,000</c>,
    /// <c>USD 55k-70k / year</c>, <c>€4,000 per month</c>) or an Indian <c>18-25 LPA</c>, and only near a pay word.
    /// Returns <see cref="Salary.None"/> when nothing trustworthy is found.
    /// </summary>
    public static Salary FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Salary.None;
        var t = text.Length > 20_000 ? text[..20_000] : text;

        foreach (Match m in Lpa().Matches(t))
        {
            if (!NearPayWord(t, m)) continue;
            var lo = Number(m.Groups["a"].Value) * 100_000;
            var hi = m.Groups["b"].Success ? Number(m.Groups["b"].Value) * 100_000 : lo;
            var s = FromStructured(lo, hi, "INR", "year", estimate: false);
            if (s.Min is not null) return s;
        }

        foreach (Match m in Range().Matches(t))
        {
            if (!NearPayWord(t, m)) continue;
            var cur = NormalizeCurrency(m.Groups["c1"].Value);
            var lo = Number(m.Groups["a"].Value) * Multiplier(m.Groups["ka"].Value);
            var hi = m.Groups["b"].Success
                ? Number(m.Groups["b"].Value) * Multiplier(m.Groups["kb"].Success ? m.Groups["kb"].Value : m.Groups["ka"].Value)
                : lo;
            var per = NormalizePeriod(m.Groups["per"].Success ? m.Groups["per"].Value : m.Groups["per1"].Value);
            if (per is null)
            {
                // No period: only amounts that clearly look annual are kept.
                if (lo < 10_000) continue;
                per = "year";
            }
            var s = FromStructured(lo, hi, cur, per, estimate: false);
            if (s.Min is not null) return s;
        }
        return Salary.None;
    }

    private static bool NearPayWord(string text, Match m)
    {
        var start = Math.Max(0, m.Index - 80);
        var end = Math.Min(text.Length, m.Index + m.Length + 40);
        return PayWord().IsMatch(text.AsSpan(start, end - start));
    }

    private static decimal Number(string s) =>
        decimal.TryParse(s.Replace(",", "", StringComparison.Ordinal), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
            ? d
            : 0;

    private static decimal Multiplier(string suffix) => suffix.ToLowerInvariant() switch
    {
        "k" => 1_000,
        "l" or "lakh" or "lakhs" or "lac" => 100_000,
        _ => 1,
    };

    // "$120,000 - $150,000 per year", "USD 55k–70k", "€4,000/month". Millions ("$3M") are never a salary here.
    [GeneratedRegex(@"(?<c1>[$€£₹]|\b(?:USD|EUR|GBP|INR|CAD|AUD|SGD|CHF)\b)\s?(?<a>\d{1,3}(?:,\d{3})+|\d+(?:\.\d+)?)\s?(?<ka>k|K|L|lakhs?|lac)?\b(?<per1>\s?(?:/|per)\s?(?:hour|hr|year|yr|month|mo|week|wk|day))?(?:\s?(?:-|–|—|to)\s?(?:[$€£₹]|\b(?:USD|EUR|GBP|INR|CAD|AUD|SGD|CHF)\b)?\s?(?<b>\d{1,3}(?:,\d{3})+|\d+(?:\.\d+)?)\s?(?<kb>k|K|L|lakhs?|lac)?\b)?(?:\s?(?:USD|EUR|GBP|INR|CAD|AUD))?\s?(?<per>(?:/|per|an|a)\s?(?:hour|hr|year|yr|annum|month|mo|week|wk|day)|hourly|annually|yearly|monthly|weekly|daily|p\.?a\.?)?",
        RegexOptions.None, 1000)]
    private static partial Regex Range();

    [GeneratedRegex(@"(?<a>\d{1,3}(?:\.\d+)?)\s?(?:-|–|to)?\s?(?<b>\d{1,3}(?:\.\d+)?)?\s?LPA\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Lpa();

    [GeneratedRegex(@"salary|compensation|\bpay\b|\bbase\b|\brange\b|\bOTE\b|\bCTC\b|LPA|per (?:year|annum|hour|month)|/yr|/hr|annual|hourly|\brate\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex PayWord();
}
