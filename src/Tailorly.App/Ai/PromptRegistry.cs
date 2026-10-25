using System.Reflection;
using System.Text.Json.Nodes;
using NJsonSchema;

namespace Tailorly.App.Ai;

/// <param name="Version">Version string from the prompt file's front matter; written to <c>ai_usage</c>.</param>
/// <param name="ModelSchema">The schema reduced to what structured outputs accept (sent to the model).</param>
public sealed record PromptTemplate(string Task, string Version, string System, JsonSchema Schema, string ModelSchema);

/// <summary>
/// Prompts are files in the repo (<c>Ai/Prompts/{task}.md</c>) with a <c>version:</c> line in their front matter, and
/// each task has a JSON schema (<c>Ai/Schemas/{task}.schema.json</c>). Both are embedded resources; git is the history
/// (SDD §9.1, S13). Loaded once at startup; a missing or malformed file fails fast.
/// </summary>
public sealed class PromptRegistry
{
    private readonly Dictionary<string, PromptTemplate> _templates = new(StringComparer.Ordinal);

    public PromptRegistry()
    {
        var asm = typeof(PromptRegistry).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("prompts/", StringComparison.Ordinal)))
        {
            var task = Path.GetFileNameWithoutExtension(name["prompts/".Length..]);
            var (version, system) = ParsePrompt(Read(asm, name), task);
            var schemaJson = Read(asm, $"schemas/{task}.schema.json");
            var schema = JsonSchema.FromJsonAsync(schemaJson).GetAwaiter().GetResult();
            _templates[task] = new PromptTemplate(task, version, system, schema, ModelSchemas.Reduce(schemaJson));
        }
    }

    public IReadOnlyCollection<string> Tasks => _templates.Keys;

    public PromptTemplate Get(string task) =>
        _templates.TryGetValue(task, out var t) ? t : throw new InvalidOperationException($"No prompt file for task '{task}'.");

    private static string Read(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    internal static (string Version, string System) ParsePrompt(string text, string task)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            throw new InvalidOperationException($"Prompt '{task}' has no front matter.");
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException($"Prompt '{task}' front matter is not closed.");
        var version = text[4..end].Split('\n')
            .Select(l => l.Split(':', 2))
            .Where(kv => kv.Length == 2 && kv[0].Trim() == "version")
            .Select(kv => kv[1].Trim())
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException($"Prompt '{task}' has no version.");
        return (version, text[(end + 5)..].Trim());
    }
}

/// <summary>Structured outputs accept a JSON-schema subset; length, range and array-size limits are checked locally.</summary>
internal static class ModelSchemas
{
    private static readonly HashSet<string> Unsupported = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "title", "minLength", "maxLength", "pattern", "minimum", "maximum", "exclusiveMinimum",
        "exclusiveMaximum", "multipleOf", "minItems", "maxItems", "uniqueItems", "minProperties", "maxProperties",
    };

    public static string Reduce(string schemaJson)
    {
        var node = JsonNode.Parse(schemaJson) ?? throw new InvalidOperationException("Empty schema.");
        Strip(node);
        return node.ToJsonString();
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).Where(Unsupported.Contains).ToList())
                    o.Remove(key);
                foreach (var (key, child) in o.ToList())
                {
                    if (key is "enum" or "const") continue;
                    if (key is "properties" or "$defs" or "definitions" && child is JsonObject map)
                        foreach (var (_, propertySchema) in map) Strip(propertySchema); // keys here are names, not keywords
                    else
                        Strip(child);
                }
                break;
            case JsonArray a:
                foreach (var child in a) Strip(child);
                break;
        }
    }
}
