using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Tailorly.Core;
using Tailorly.Core.Budgets;
using Tailorly.Core.Ports;

namespace Tailorly.App.Ai;

public sealed class AiOptions
{
    /// <summary><c>Fake</c> (fixtures, no key) or <c>Anthropic</c>.</summary>
    public string Provider { get; set; } = "Fake";

    /// <summary>Global limit on concurrent model calls across the process (SDD §11.4).</summary>
    public int MaxConcurrentCalls { get; set; } = 4;

    /// <summary>Reasoning effort sent with every call unless a task overrides it (extraction tasks need little).</summary>
    public string? Effort { get; set; } = "low";

    /// <summary>Optional per-task overrides, e.g. <c>Ai:Tasks:resume_tailor:Effort=high</c>.</summary>
    public Dictionary<string, AiTaskOptions> Tasks { get; set; } = new(StringComparer.Ordinal);

    public string? EffortFor(string task) => Tasks.TryGetValue(task, out var t) && t.Effort is not null ? t.Effort : Effort;
}

public sealed class AiTaskOptions
{
    public string? Effort { get; set; }
}

/// <summary>Bounds concurrent model calls for the whole process.</summary>
public sealed class AiConcurrency(IOptions<AiOptions> options) : IDisposable
{
    public SemaphoreSlim Gate { get; } = new(Math.Max(1, options.Value.MaxConcurrentCalls));
    public void Dispose() => Gate.Dispose();
}

/// <summary>
/// SDD §9.1: config → prompt → budget check → provider (global semaphore) → schema validation → domain validation →
/// atomic budget increment → usage ledger. One repair attempt on invalid output, then a clean failure. Every call is
/// charged its actual usage, the repair included, and nothing else. Logs ids, sizes, counts and durations only.
/// </summary>
public sealed partial class AiTaskRunner(
    TaskConfigCache configs,
    PromptRegistry prompts,
    IModelProvider provider,
    AiConcurrency concurrency,
    BudgetService budgets,
    UsageLedger ledger,
    IOptions<AiOptions> options,
    ILogger<AiTaskRunner> logger) : IAiTaskRunner
{
    private const int MaxRepairEcho = 20_000;
    private const int MaxErrorsInRepair = 15;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AiTaskResult<T>> RunAsync<T>(AiTask<T> task, AiTaskInput input, Guid? userId, CancellationToken ct)
    {
        var cfg = await configs.GetAsync(task.Name, ct)
            ?? throw AiTaskFailedException.Unavailable($"Task '{task.Name}' is not configured.", transient: false);
        if (!cfg.Enabled)
            throw AiTaskFailedException.Unavailable($"Task '{task.Name}' is disabled.", transient: false);

        var prompt = prompts.Get(task.Name);
        var charged = userId is not null && BudgetBuckets.IsPerUser(cfg.Bucket);
        var period = budgets.CurrentPeriod;
        var messages = new List<ModelMessage> { new(ModelRole.User, BuildUserContent(input.Data)) };
        long countedTotal = 0;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (charged)
                await budgets.EnsureRemainingAsync(userId!.Value, cfg.Bucket, ct);

            var request = new ModelRequest(task.Name, cfg.Model, prompt.System, input.CacheableContext, messages,
                cfg.MaxTokens, prompt.ModelSchema, options.Value.EffortFor(task.Name));

            var sw = Stopwatch.StartNew();
            ModelResponse response;
            await concurrency.Gate.WaitAsync(ct);
            try
            {
                response = await provider.SendAsync(request, ct);
            }
            catch (ModelProviderException ex)
            {
                var ms = (int)sw.ElapsedMilliseconds;
                await ledger.WriteAsync(userId, task.Name, cfg.Model, prompt.Version, cfg.Bucket, period, ex.Usage, ms, false,
                    ErrorCodes.AiUnavailable);
                if (charged) await budgets.AddAsync(userId!.Value, cfg.Bucket, ex.Usage.CountedTokens, CancellationToken.None);
                LogProviderFailed(logger, task.Name, attempt, ex.StatusCode, ex.Transient, ms);
                throw AiTaskFailedException.Unavailable("The AI provider is unavailable.", ex.Transient, ex);
            }
            finally
            {
                concurrency.Gate.Release();
            }
            var latency = (int)sw.ElapsedMilliseconds;
            var counted = response.Usage.CountedTokens;
            countedTotal += counted;
            // Charge actual usage even if the caller was cancelled meanwhile: the tokens were spent.
            if (charged) await budgets.AddAsync(userId!.Value, cfg.Bucket, counted, CancellationToken.None);

            var (value, json, errors) = Validate(task, prompt, response);
            await ledger.WriteAsync(userId, task.Name, response.Model, prompt.Version, cfg.Bucket, period, response.Usage, latency,
                errors.Count == 0, errors.Count == 0 ? null : ErrorCodes.AiInvalidOutput);
            LogCall(logger, task.Name, attempt, response.Model, response.Usage.InputTokens, response.Usage.OutputTokens,
                response.Usage.CacheReadTokens, latency, errors.Count, response.StopReason);

            if (errors.Count == 0)
                return new AiTaskResult<T>(value!, json!, response.Model, prompt.Version, countedTotal);

            // A refusal will not change on repair; anything else gets one chance with the problems listed.
            if (attempt == 2 || response.StopReason == "refusal") break;
            messages =
            [
                messages[0],
                new(ModelRole.Assistant, Truncate(response.Text, MaxRepairEcho)),
                new(ModelRole.User, RepairInstruction(errors)),
            ];
        }

        throw new AiTaskFailedException(ErrorCodes.AiInvalidOutput, transient: false,
            $"Task '{task.Name}' returned invalid output after one repair attempt.");
    }

    /// <summary>Wraps untrusted text in delimiters; any delimiter-like text inside is neutralised (prompt injection).</summary>
    internal static string BuildUserContent(IReadOnlyList<UntrustedBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            var safe = DelimiterLike().Replace(b.Content, m => m.Value.Replace('<', '[').Replace('>', ']'));
            // "\n" on every platform: the prompt bytes must not depend on the OS (prompt caching, tests).
            sb.Append('<').Append(b.Tag).Append(">\n")
              .Append(safe.Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n')
              .Append("</").Append(b.Tag).Append(">\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static (T? Value, string? Json, List<string> Errors) Validate<T>(AiTask<T> task, PromptTemplate prompt, ModelResponse r)
    {
        if (r.StopReason == "refusal") return (default, null, ["The request was declined."]);
        if (r.StopReason == "max_tokens") return (default, null, ["The reply was cut off before the JSON was complete; return a shorter reply."]);

        var json = StripCodeFence(r.Text);
        if (json.Length == 0) return (default, null, ["The reply was empty."]);
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (default, null, ["The reply was not valid JSON."]);
        }

        var schemaErrors = prompt.Schema.Validate(json)
            .Select(e => $"{e.Kind} at {(string.IsNullOrEmpty(e.Path) ? "#" : e.Path)}")
            .Distinct()
            .ToList();
        if (schemaErrors.Count > 0) return (default, null, schemaErrors);

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return (default, null, ["The JSON did not match the expected shape."]);
        }
        if (value is null) return (default, null, ["The reply was null."]);

        var domain = task.Validate(value);
        return domain.Count > 0 ? (default, null, domain.ToList()) : (value, json, []);
    }

    private static string RepairInstruction(IReadOnlyList<string> errors)
    {
        var sb = new StringBuilder("Your previous reply did not pass validation:\n");
        foreach (var e in errors.Take(MaxErrorsInRepair)) sb.Append("- ").AppendLine(e);
        sb.Append("Reply again with only the corrected JSON object that follows the required schema. ")
          .Append("Keep using only information from the data provided.");
        return sb.ToString();
    }

    internal static string StripCodeFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        var firstNewline = t.IndexOf('\n');
        if (firstNewline < 0) return "";
        t = t[(firstNewline + 1)..];
        var close = t.LastIndexOf("```", StringComparison.Ordinal);
        return (close >= 0 ? t[..close] : t).Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"</?\s*[a-z_]+_data\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex DelimiterLike();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "AI task {Task} attempt {Attempt} model {Model}: in {InputTokens} out {OutputTokens} cache_read {CacheReadTokens} " +
                  "in {LatencyMs} ms, {ValidationErrors} validation error(s), stop {StopReason}")]
    private static partial void LogCall(ILogger logger, string task, int attempt, string model, int inputTokens, int outputTokens,
        int cacheReadTokens, int latencyMs, int validationErrors, string? stopReason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "AI task {Task} attempt {Attempt} provider failure (status {StatusCode}, transient {Transient}) after {LatencyMs} ms")]
    private static partial void LogProviderFailed(ILogger logger, string task, int attempt, int? statusCode, bool transient, int latencyMs);
}
