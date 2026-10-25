namespace Tailorly.App.Ai.Tasks;

// Shape of resume_profiles.parsed (DatabaseDesign §4); JSON is camelCase, matching Ai/Schemas/resume_parse.schema.json.

public sealed record ParsedProfile(
    ParsedContact Contact,
    string? Summary,
    IReadOnlyList<ParsedSkill> Skills,
    IReadOnlyList<ParsedRole> Experience,
    IReadOnlyList<ParsedEducation> Education,
    IReadOnlyList<ParsedCertification> Certifications,
    double? TotalYears,
    IReadOnlyList<string> Domains);

public sealed record ParsedContact(string? Name, string? Email, string? Phone, string? Location, IReadOnlyList<string> Links);

public sealed record ParsedSkill(string Name, string Category);

public sealed record ParsedRole(string Company, string Title, string? Start, string? End, IReadOnlyList<string> Bullets);

public sealed record ParsedEducation(string? Institution, string? Degree, string? Field, string? Start, string? End);

public sealed record ParsedCertification(string Name, string? Issuer, string? Year);

/// <summary><c>resume_parse</c>: structured profile from the résumé text (PRD F2). Charged to Documents.</summary>
public static class ResumeParseTask
{
    public static readonly AiTask<ParsedProfile> Definition = new(AiTaskNames.ResumeParse, Validate);

    /// <summary>Deterministic checks the schema cannot express. Messages carry no résumé content.</summary>
    internal static IReadOnlyList<string> Validate(ParsedProfile p)
    {
        var errors = new List<string>();
        for (var i = 0; i < p.Experience.Count; i++)
        {
            var r = p.Experience[i];
            if (string.IsNullOrWhiteSpace(r.Company) && string.IsNullOrWhiteSpace(r.Title))
                errors.Add($"experience[{i}] has neither a company nor a title.");
        }
        for (var i = 0; i < p.Skills.Count; i++)
            if (string.IsNullOrWhiteSpace(p.Skills[i].Name))
                errors.Add($"skills[{i}].name is blank.");
        if (p.TotalYears is { } y && (double.IsNaN(y) || y < 0 || y > 60))
            errors.Add("totalYears is outside 0–60.");
        return errors;
    }
}
