namespace Tailorly.Core.Ports;

/// <summary>
/// Sends one request to an LLM. The model is never given tools; it can only return text (JSON).
/// Model ids come from <c>ai_task_config</c>, never from code.
/// </summary>
public interface IModelProvider
{
    /// <exception cref="ModelProviderException">The provider failed after its own retries.</exception>
    Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct);
}

/// <param name="Task">Task name (e.g. <c>resume_parse</c>); used by fakes to pick a fixture and for usage records.</param>
/// <param name="System">Task instructions. Stable, so they are marked for prompt caching.</param>
/// <param name="CacheablePrefix">Optional stable context after the instructions (e.g. the parsed profile), also cached.</param>
/// <param name="Messages">The conversation: one user turn, or user/assistant/user for the repair attempt.</param>
/// <param name="JsonSchema">JSON schema (subset accepted by structured outputs) the reply must follow, or null.</param>
/// <param name="Effort">Reasoning effort (<c>low</c>, <c>medium</c>, <c>high</c>, ...), or null for the model default.</param>
/// <remarks>
/// No sampling parameters: API version 2026-10-21 rejects <c>temperature</c>/<c>top_p</c>/<c>top_k</c> on every model
/// (see DECISIONS.md). <c>ai_task_config.temperature</c> is kept in the schema but not sent.
/// </remarks>
public sealed record ModelRequest(
    string Task,
    string Model,
    string System,
    string? CacheablePrefix,
    IReadOnlyList<ModelMessage> Messages,
    int MaxTokens,
    string? JsonSchema,
    string? Effort);

public enum ModelRole
{
    User,
    Assistant,
}

public sealed record ModelMessage(ModelRole Role, string Text);

/// <param name="Text">Concatenated text blocks of the reply (thinking blocks are not included).</param>
/// <param name="StopReason">Provider stop reason, e.g. <c>end_turn</c>, <c>max_tokens</c>, <c>refusal</c>.</param>
public sealed record ModelResponse(string Text, ModelUsage Usage, string Model, string? StopReason);

public sealed record ModelUsage(int InputTokens, int OutputTokens, int CacheCreationTokens, int CacheReadTokens)
{
    public static readonly ModelUsage Zero = new(0, 0, 0, 0);

    /// <summary>PRD §6.3: input + output + cache_creation + 0.1 × cache_read.</summary>
    public long CountedTokens => InputTokens + OutputTokens + CacheCreationTokens + (long)Math.Ceiling(CacheReadTokens * 0.1);
}

/// <summary>
/// The provider could not produce a reply. <see cref="Transient"/> failures (rate limits, overload, 5xx, network) may
/// succeed later and are retried by the background job; the others (bad request, auth) will not.
/// </summary>
public sealed class ModelProviderException(string message, bool transient, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Transient { get; } = transient;
    public int? StatusCode { get; } = statusCode;

    /// <summary>Usage reported for a call that failed after the provider billed it (rare); otherwise zero.</summary>
    public ModelUsage Usage { get; init; } = ModelUsage.Zero;
}
