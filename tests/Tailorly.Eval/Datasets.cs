using System.Text.Json;
using System.Text.Json.Serialization;
using NJsonSchema;
using Tailorly.App.Ai.Tasks;

namespace Tailorly.Eval;

/// <summary>
/// One labelled job for the eligibility eval (PRD §14.3: ≥ 100 descriptions). <see cref="Label"/> is the human answer
/// to "can a candidate living in India be hired?"; <see cref="LabelSource"/> says who labelled it (starter items are
/// marked unreviewed until the owner confirms them).
/// </summary>
public sealed record EligibilityItem(
    string Id,
    string Title,
    string Company,
    string? LocationText,
    string? WorkMode,
    string? EmploymentType,
    IReadOnlyList<string>? Countries,
    string? Description,
    string Label,
    string LabelSource,
    string? Notes);

/// <summary>The candidate side of a fit pair: the parsed profile shape the app stores, plus fit preferences.</summary>
public sealed record FitCandidate(ParsedProfile Profile, IReadOnlyList<string> ConfirmedSkills, FitPreferences Preferences);

/// <summary>The job side of a fit pair: listing fields plus its enrichment (as <c>job_enrichment</c> would store it).</summary>
public sealed record FitJob(
    string Title,
    string Company,
    string? LocationText,
    string? WorkMode,
    string? EmploymentType,
    string Eligibility,
    string DescriptionCompleteness,
    EnrichmentOutput Enrichment);

/// <summary>One labelled (candidate, job) pair for the fit-score eval (PRD §14.3: ≥ 50 pairs). Label is 1–10.</summary>
public sealed record FitItem(string Id, string CandidateId, FitJob Job, int Label, string LabelSource, string? Notes);

public sealed record EligibilityDataset(int SchemaVersion, IReadOnlyList<EligibilityItem> Items);

public sealed record FitDataset(int SchemaVersion, IReadOnlyDictionary<string, FitCandidate> Candidates, IReadOnlyList<FitItem> Items);

/// <summary>Loads a dataset after validating it against its JSON schema (same folder, <c>{name}.schema.json</c>).</summary>
public static class Datasets
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static async Task<(T? Data, IReadOnlyList<string> Errors)> LoadAsync<T>(string folder, string name)
    {
        var dataPath = Path.Combine(folder, name + ".json");
        var schemaPath = Path.Combine(folder, name + ".schema.json");
        if (!File.Exists(dataPath)) return (default, [$"{name}.json not found in {folder}"]);
        var text = await File.ReadAllTextAsync(dataPath);
        var schema = await JsonSchema.FromJsonAsync(await File.ReadAllTextAsync(schemaPath));
        var errors = schema.Validate(text).Select(e => $"{name}: {e.Kind} at {e.Path}").ToList();
        if (errors.Count > 0) return (default, errors);
        var data = JsonSerializer.Deserialize<T>(text, Json);
        return data is null ? (default, [$"{name}: empty"]) : (data, []);
    }
}
