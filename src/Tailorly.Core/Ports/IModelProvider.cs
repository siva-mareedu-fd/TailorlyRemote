namespace Tailorly.Core.Ports;

/// <summary>
/// Sends one request to an LLM. The model is never given tools; it can only return text (JSON).
/// Model ids come from <c>ai_task_config</c>, never from code.
/// </summary>
public interface IModelProvider
{
    Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct);
}

/// <param name="Task">Task name (e.g. <c>resume_parse</c>); used by fakes to pick a fixture and for usage records.</param>
/// <param name="CacheablePrefix">Stable prefix (instructions + profile) marked for prompt caching.</param>
public sealed record ModelRequest(
    string Task,
    string Model,
    string System,
    string? CacheablePrefix,
    string UserContent,
    int MaxTokens,
    decimal Temperature);

public sealed record ModelResponse(string Text, ModelUsage Usage, string Model, string? StopReason);

public sealed record ModelUsage(int InputTokens, int OutputTokens, int CacheCreationTokens, int CacheReadTokens)
{
    /// <summary>PRD §6.3: input + output + cache_creation + 0.1 × cache_read.</summary>
    public long CountedTokens => InputTokens + OutputTokens + CacheCreationTokens + (long)Math.Ceiling(CacheReadTokens * 0.1);
}
