using System.Text.RegularExpressions;

namespace Tailorly.Core.Jobs;

public static class Eligibility
{
    public const string Worldwide = "worldwide";
    public const string IndiaEligible = "india_eligible";
    public const string Restricted = "restricted";
    public const string Unclear = "unclear";

    public static bool OpenToIndia(string eligibility) => eligibility is Worldwide or IndiaEligible;
}

/// <param name="Evidence">A short quote (≤ 120 characters) or a description of the structured field that decided it.</param>
public sealed record EligibilityHint(string Eligibility, string? Evidence);

/// <summary>
/// Deterministic eligibility pre-rules (PRD §7.4, SDD §8.6), run before any model call:
/// <list type="number">
/// <item>structured source fields (Himalayas country restrictions): empty → worldwide, India listed → india_eligible,
/// otherwise restricted;</item>
/// <item>an India location (city, "India", APAC) in the location or title → india_eligible;</item>
/// <item>a hard restriction that excludes India ("US only", "must reside in the EU", "authorized to work in the United
/// States", a non-Indian country or US state as the location) → restricted;</item>
/// <item>"anywhere/worldwide" wording → worldwide; India named in the description → india_eligible;</item>
/// <item>otherwise unclear.</item>
/// </list>
/// The M3 enrichment receives this as a hint; hard restrictions still win over the model (SDD §8.6). The coverage report
/// uses it for jobs not yet enriched.
/// </summary>
public static partial class EligibilityRules
{
    public static EligibilityHint Classify(IReadOnlyList<string>? countryRestrictions, string? locationText, string? title,
        string? descriptionText)
    {
        if (countryRestrictions is not null)
        {
            if (countryRestrictions.Count == 0) return new(Eligibility.Worldwide, "Open worldwide (source location field)");
            return countryRestrictions.Any(c => IndiaCountry().IsMatch(c))
                ? new(Eligibility.IndiaEligible, Quote("Open to: " + string.Join(", ", countryRestrictions)))
                : new(Eligibility.Restricted, Quote("Limited to: " + string.Join(", ", countryRestrictions)));
        }

        var location = locationText ?? "";
        var head = $"{location} | {title}";
        var description = descriptionText is { Length: > 30_000 } d ? d[..30_000] : descriptionText ?? "";

        if (IndiaPlace().Match(head) is { Success: true } india) return new(Eligibility.IndiaEligible, Around(head, india));
        if (Restriction().Match(head) is { Success: true } r1) return new(Eligibility.Restricted, Around(head, r1));
        if (ForeignLocation().Match(location) is { Success: true } foreign && !Worldwide().IsMatch(location))
            return new(Eligibility.Restricted, Around(location, foreign));
        if (UsState().Match(location) is { Success: true } state) return new(Eligibility.Restricted, Around(location, state));
        if (Restriction().Match(description) is { Success: true } r2) return new(Eligibility.Restricted, Around(description, r2));
        if (Worldwide().Match(head) is { Success: true } w1) return new(Eligibility.Worldwide, Around(head, w1));
        if (Worldwide().Match(description) is { Success: true } w2) return new(Eligibility.Worldwide, Around(description, w2));
        if (IndiaPlace().Match(description) is { Success: true } i2) return new(Eligibility.IndiaEligible, Around(description, i2));
        return new(Eligibility.Unclear, null);
    }

    private static string Around(string text, Match m)
    {
        var start = Math.Max(0, m.Index - 40);
        var end = Math.Min(text.Length, m.Index + m.Length + 40);
        return Quote(text[start..end]);
    }

    private static string Quote(string s)
    {
        var t = HtmlText.Tidy(s).Replace('\n', ' ').Trim(' ', '|');
        return t.Length <= 120 ? t : t[..117] + "...";
    }

    [GeneratedRegex(@"^\s*(india|in|ind)\s*$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex IndiaCountry();

    [GeneratedRegex(@"\b(india|bengaluru|bangalore|hyderabad|pune|mumbai|new delhi|delhi|ncr|gurgaon|gurugram|noida|chennai|kolkata|ahmedabad|kochi|jaipur|coimbatore|apac|asia[- ]pacific|asia)\b",
        RegexOptions.IgnoreCase, 1000)]
    private static partial Regex IndiaPlace();

    private const string Regions =
        @"(the )?(us|u\.s\.?|usa|united states( of america)?|uk|u\.k\.|united kingdom|canada|eu|e\.u\.|european union|europe|eea|emea|latam|latin america|north america|americas|australia|germany|france|spain|brazil|mexico)";

    [GeneratedRegex(
        @"\b(us|u\.s\.|usa|uk|eu|europe|emea|canada|latam|north america|americas)[- ]only\b" +
        @"|\bonly (open to|accepting|hiring|considering) (candidates |applicants |residents )?(based |located |residing )?(in|from) " + Regions + @"\b" +
        @"|\bmust (be )?(currently )?(reside|resident|residing|located|based|live|living) (in|within) " + Regions + @"\b" +
        @"|\b(authori[sz]ed|eligible|legally (able|authorized)|permitted) to work (in|for) " + Regions + @"\b" +
        @"|\b(work authori[sz]ation|right to work) (in|for) " + Regions + @"\b" +
        @"|\b(candidates|applicants|residents|employees) (must be )?(based|located|residing) in " + Regions + @"\b" +
        @"|\bwithin the (us|u\.s\.|usa|united states|uk|united kingdom|eu)\b",
        RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Restriction();

    // Case-sensitive for short codes (so "Join us" is not the US); names are matched case-insensitively.
    [GeneratedRegex(@"\b(US|USA|U\.S\.|UK|EU|EMEA|LATAM)\b|\b(?i:united states|united kingdom|england|scotland|ireland|canada|germany|france|spain|portugal|netherlands|belgium|poland|italy|sweden|norway|denmark|finland|switzerland|austria|czechia|czech republic|romania|ukraine|israel|turkey|brazil|mexico|argentina|colombia|chile|peru|australia|new zealand|japan|singapore|philippines|indonesia|vietnam|malaysia|thailand|south korea|china|hong kong|taiwan|south africa|nigeria|kenya|egypt|uae|united arab emirates|dubai|saudi arabia|europe|north america|latin america|americas)\b",
        RegexOptions.None, 1000)]
    private static partial Regex ForeignLocation();

    [GeneratedRegex(@",\s*(AL|AK|AZ|AR|CA|CO|CT|DE|FL|GA|HI|ID|IL|IA|KS|KY|LA|ME|MD|MA|MI|MN|MS|MO|MT|NE|NV|NH|NJ|NM|NY|NC|ND|OH|OK|OR|PA|RI|SC|SD|TN|TX|UT|VT|VA|WA|WV|WI|WY|DC)\b",
        RegexOptions.None, 1000)]
    private static partial Regex UsState();

    [GeneratedRegex(@"\b(anywhere in the world|work from anywhere|from anywhere|worldwide|anywhere|global(ly)? remote|remote[- ]global|any location|any country|fully distributed)\b",
        RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Worldwide();
}
