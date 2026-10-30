using Tailorly.Core;

namespace Tailorly.App.Ai;

/// <summary>Task names; each has a row in <c>ai_task_config</c>, a prompt file and a schema file.</summary>
public static class AiTaskNames
{
    public const string ResumeParse = "resume_parse";
    public const string JobEnrichment = "job_enrichment";
    public const string JobFitScore = "job_fit_score";
}

/// <summary>
/// A task the runner can execute: its name and the deterministic domain validator applied after schema validation.
/// The validator returns problems as short, content-free messages (they are sent back in the repair prompt and counted
/// in logs, never logged verbatim).
/// </summary>
public sealed record AiTask<T>(string Name, Func<T, IReadOnlyList<string>> Validate);

/// <summary>Untrusted text (résumé, job description) sent inside <c>&lt;Tag&gt;…&lt;/Tag&gt;</c> delimiters.</summary>
public sealed record UntrustedBlock(string Tag, string Content);

/// <param name="CacheableContext">Stable context placed after the instructions and cached with them (e.g. the profile).</param>
public sealed record AiTaskInput(IReadOnlyList<UntrustedBlock> Data, string? CacheableContext = null);

public sealed record AiTaskResult<T>(T Value, string Json, string Model, string PromptVersion, long CountedTokens);

/// <summary>Runs one AI task end to end (SDD §9.1). The model gets no tools and can only return validated JSON.</summary>
public interface IAiTaskRunner
{
    /// <param name="userId">The user charged (null for platform tasks such as job enrichment).</param>
    /// <exception cref="BudgetExceededException">The user's bucket has no tokens left.</exception>
    /// <exception cref="AiTaskFailedException">Provider failure or invalid output after one repair.</exception>
    Task<AiTaskResult<T>> RunAsync<T>(AiTask<T> task, AiTaskInput input, Guid? userId, CancellationToken ct);
}

/// <summary>The bucket has no remaining tokens this period (409 BUDGET_EXCEEDED with bucket and reset date).</summary>
public sealed class BudgetExceededException(string bucket, DateOnly resetsOn)
    : Exception($"The {bucket} budget is used up until {resetsOn:yyyy-MM-dd}.")
{
    public string Bucket { get; } = bucket;
    public DateOnly ResetsOn { get; } = resetsOn;
}

/// <summary>
/// The task did not produce a usable result. <see cref="Code"/> is the resource <c>error_code</c>
/// (<c>AI_UNAVAILABLE</c> or <c>AI_INVALID_OUTPUT</c>). <see cref="Transient"/> failures should be retried later.
/// </summary>
public sealed class AiTaskFailedException(string code, bool transient, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool Transient { get; } = transient;

    public static AiTaskFailedException Unavailable(string message, bool transient, Exception? inner = null) =>
        new(ErrorCodes.AiUnavailable, transient, message, inner);
}
