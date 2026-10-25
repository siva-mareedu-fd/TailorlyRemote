using System.Text.Json;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Docx;

namespace Tailorly.App.Features.Resumes;

// HTTP contract of ApiDesign §4.4.

/// <param name="ParseStatus">queued | running | ready | failed (poll <c>GET /resumes/{id}</c>).</param>
public sealed record ResumeDto(
    Guid Id,
    int Version,
    string OriginalFilename,
    int SizeBytes,
    bool IsActive,
    string ParseStatus,
    string? ErrorCode,
    FormatFindings? FormatFindings,
    DateTimeOffset CreatedAt)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ResumeDto From(Resume r) => new(
        r.Id, r.Version, r.OriginalFilename, r.SizeBytes, r.IsActive, r.ParseStatus, r.ErrorCode,
        r.FormatFindings is null ? null : JsonSerializer.Deserialize<FormatFindings>(r.FormatFindings, Json),
        r.CreatedAt);
}

/// <summary>
/// User corrections laid over the parsed profile. Each non-null field replaces the parsed one as a whole (e.g. the full
/// skills list); null means "not corrected".
/// </summary>
public sealed record ProfileEdits(
    ParsedContact? Contact = null,
    string? Summary = null,
    IReadOnlyList<ParsedSkill>? Skills = null,
    IReadOnlyList<ParsedRole>? Experience = null,
    IReadOnlyList<ParsedEducation>? Education = null,
    IReadOnlyList<ParsedCertification>? Certifications = null,
    double? TotalYears = null,
    IReadOnlyList<string>? Domains = null)
{
    public ParsedProfile ApplyTo(ParsedProfile p) => new(
        Contact ?? p.Contact,
        Summary ?? p.Summary,
        Skills ?? p.Skills,
        Experience ?? p.Experience,
        Education ?? p.Education,
        Certifications ?? p.Certifications,
        TotalYears ?? p.TotalYears,
        Domains ?? p.Domains);
}

/// <param name="Effective">What matching and tailoring use: <see cref="UserEdited"/> merged over <see cref="Parsed"/>.</param>
public sealed record ResumeProfileDto(ParsedProfile Parsed, ProfileEdits? UserEdited, ParsedProfile Effective, DateTimeOffset UpdatedAt);

public sealed record UpdateProfileRequest(ProfileEdits? UserEdited);

public sealed record ConfirmedSkillDto(Guid Id, string Skill, DateTimeOffset CreatedAt)
{
    public static ConfirmedSkillDto From(UserConfirmedSkill s) => new(s.Id, s.Skill, s.CreatedAt);
}

public sealed record AddConfirmedSkillRequest(string? Skill);

/// <summary>OpenAPI shape of <c>POST /resumes</c>: multipart/form-data with one field <c>file</c> (read manually).</summary>
public sealed record ResumeUploadForm(IFormFile File);
