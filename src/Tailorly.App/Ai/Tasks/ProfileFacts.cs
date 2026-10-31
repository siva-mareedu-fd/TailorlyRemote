using System.Globalization;

namespace Tailorly.App.Ai.Tasks;

/// <summary>Every statement in a parsed (and user-corrected) profile, as text, for the truthfulness checks.</summary>
public static class ProfileFacts
{
    public static IEnumerable<string> Strings(ParsedProfile p)
    {
        if (p.Contact is { } c)
        {
            if (c.Name is { } name) yield return name;
            if (c.Email is { } email) yield return email;
            if (c.Phone is { } phone) yield return phone;
            if (c.Location is { } location) yield return location;
            foreach (var link in c.Links) yield return link;
        }
        if (p.Summary is { } summary) yield return summary;
        foreach (var s in p.Skills) yield return s.Name;
        foreach (var r in p.Experience)
        {
            yield return $"{r.Title}, {r.Company} ({r.Start} – {r.End})";
            foreach (var b in r.Bullets) yield return b;
        }
        foreach (var e in p.Education) yield return string.Join(", ", new[] { e.Degree, e.Field, e.Institution, e.Start, e.End }.Where(x => x is not null));
        foreach (var cert in p.Certifications) yield return string.Join(", ", new[] { cert.Name, cert.Issuer, cert.Year }.Where(x => x is not null));
        foreach (var d in p.Domains) yield return d;
        if (p.TotalYears is { } years) yield return years.ToString("0.#", CultureInfo.InvariantCulture) + " years";
    }
}
