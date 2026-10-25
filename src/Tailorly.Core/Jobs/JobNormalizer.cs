using System.Security.Cryptography;
using System.Text;
using Tailorly.Core.Ports;

namespace Tailorly.Core.Jobs;

/// <summary>A normalised listing, one-to-one with a <c>jobs</c> row (PRD §7.4).</summary>
public sealed record JobPosting
{
    public required string SourceKey { get; init; }
    public required string ExternalId { get; init; }
    public required string DedupeKey { get; init; }
    public required string CompanyName { get; init; }
    public required string Title { get; init; }
    public string? LocationText { get; init; }
    public required string LocationClass { get; init; }
    public string? WorkMode { get; init; }
    public string? EmploymentType { get; init; }
    public string? HiringModelHint { get; init; }
    public required Salary Salary { get; init; }
    public required string DescriptionText { get; init; }
    public required bool DescriptionFull { get; init; }
    public required string ApplyUrl { get; init; }
    public required string SourceUrl { get; init; }
    public string? ContentHash { get; init; }
    public DateTimeOffset? PostedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public required EligibilityHint EligibilityHint { get; init; }
    public IReadOnlyList<string>? CountryRestrictions { get; init; }
    public string? CompanyWebsite { get; init; }
}

/// <summary>What the normaliser needs to know about the source (from <c>job_sources</c>).</summary>
/// <param name="RemoteOnly">A remote job board (every feed source): listings default to remote.</param>
public sealed record SourceTraits(bool RemoteOnly);

/// <summary>
/// <c>RawJob → clean text → work mode / employment type → salary → dedupe key → JobPosting</c> (SDD §8.2). Pure: no I/O,
/// no clock, so every rule is unit-tested.
/// </summary>
public static class JobNormalizer
{
    /// <summary>Plain text shorter than this is a teaser, not a description (real ones run to thousands of characters).</summary>
    public const int MinFullDescriptionChars = 400;

    public const int MaxTitleChars = 300;
    public const int MaxCompanyChars = 200;
    public const int MaxLocationChars = 300;

    public static JobPosting Normalize(RawJob raw, SourceTraits traits)
    {
        var title = Clip(HtmlText.Tidy(System.Net.WebUtility.HtmlDecode(raw.Title)).Replace('\n', ' '), MaxTitleChars);
        var company = Clip(HtmlText.Tidy(System.Net.WebUtility.HtmlDecode(raw.CompanyName)).Replace('\n', ' '), MaxCompanyChars);
        var location = string.IsNullOrWhiteSpace(raw.LocationText) ? null : Clip(raw.LocationText.Trim(), MaxLocationChars);
        var text = HtmlText.ToPlainText(raw.DescriptionHtml);

        var workMode = JobFields.WorkMode(raw.WorkplaceType, location, traits.RemoteOnly);
        var locationClass = JobFields.LocationClass(workMode, raw.CountryRestrictions, location);

        var salary = SalaryParser.FromStructured(raw.SalaryMin, raw.SalaryMax, raw.SalaryCurrency, raw.SalaryPeriod, raw.SalaryIsEstimate);
        if (salary.Kind == SalaryKinds.Unknown) salary = SalaryParser.FromText(text);

        return new JobPosting
        {
            SourceKey = raw.SourceKey,
            ExternalId = raw.ExternalId,
            DedupeKey = DedupeKey.Compute(company, title, locationClass),
            CompanyName = company,
            Title = title,
            LocationText = location,
            LocationClass = locationClass,
            WorkMode = workMode,
            EmploymentType = JobFields.EmploymentType(raw.EmploymentType),
            HiringModelHint = JobFields.HiringModelHint(raw.EmploymentType),
            Salary = salary,
            DescriptionText = text,
            DescriptionFull = !raw.DescriptionIsPartial && text.Length >= MinFullDescriptionChars,
            ApplyUrl = raw.ApplyUrl.Trim(),
            SourceUrl = raw.SourceUrl.Trim(),
            ContentHash = text.Length == 0 ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            PostedAt = raw.PostedAt,
            ExpiresAt = raw.ExpiresAt,
            EligibilityHint = EligibilityRules.Classify(raw.CountryRestrictions, location, title, text),
            CountryRestrictions = raw.CountryRestrictions,
            CompanyWebsite = raw.CompanyWebsite,
        };
    }

    /// <summary>True for an absolute http(s) URL (apply and source links are shown to users; ApiDesign §14.2).</summary>
    public static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" && url!.Length <= 2048;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
