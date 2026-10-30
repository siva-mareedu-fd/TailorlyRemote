using System.Diagnostics;
using Tailorly.App.Ai;
using Tailorly.Core.Ports;

namespace Tailorly.Eval;

/// <param name="Value">The validated reply, or default when it failed validation after one repair.</param>
/// <param name="ValidFirstTry">The first reply passed schema and domain validation.</param>
/// <param name="Error">Validation messages of the last failed reply (also set when only the first reply failed).</param>
public sealed record TaskOutcome<T>(T? Value, bool ValidFirstTry, bool Valid, long CountedTokens, int LatencyMs, string? Error);

/// <summary>
/// The production call path without the database: the task's prompt file, schema, input builder and validators, one
/// repair turn, then a verdict (SDD §9.4). Budgets and the usage ledger are not involved; nothing is persisted.
/// </summary>
public sealed class ModelRunner(IModelProvider provider, PromptRegistry prompts, string? effort)
{
    public async Task<TaskOutcome<T>> RunAsync<T>(AiTask<T> task, AiTaskInput input, string model, int maxTokens, CancellationToken ct)
    {
        var prompt = prompts.Get(task.Name);
        var messages = new List<ModelMessage> { new(ModelRole.User, AiTaskRunner.BuildUserContent(input.Data)) };
        long tokens = 0;
        var sw = Stopwatch.StartNew();
        string? firstErrors = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            ModelResponse response;
            try
            {
                response = await provider.SendAsync(new ModelRequest(task.Name, model, prompt.System, input.CacheableContext, messages,
                    maxTokens, prompt.ModelSchema, effort), ct);
            }
            catch (ModelProviderException ex)
            {
                return new TaskOutcome<T>(default, false, false, tokens, (int)sw.ElapsedMilliseconds, $"provider: {ex.StatusCode}");
            }
            tokens += response.Usage.CountedTokens;
            var (value, _, errors) = AiTaskRunner.Validate(task, prompt, response);
            if (errors.Count == 0)
                return new TaskOutcome<T>(value, attempt == 1, true, tokens, (int)sw.ElapsedMilliseconds,
                    firstErrors is null ? null : "repaired: " + firstErrors);
            if (attempt == 2 || response.StopReason == "refusal")
                return new TaskOutcome<T>(default, false, false, tokens, (int)sw.ElapsedMilliseconds, string.Join("; ", errors.Take(3)));
            firstErrors = string.Join("; ", errors.Take(3));
            messages = [messages[0], new(ModelRole.Assistant, response.Text), new(ModelRole.User, AiTaskRunner.RepairInstruction(errors))];
        }
        return new TaskOutcome<T>(default, false, false, tokens, (int)sw.ElapsedMilliseconds, "unreachable");
    }
}
