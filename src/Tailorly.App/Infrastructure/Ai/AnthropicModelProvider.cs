using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Tailorly.Core.Ports;

namespace Tailorly.App.Infrastructure.Ai;

public sealed class AnthropicOptions
{
    /// <summary>From the environment (<c>Anthropic__ApiKey</c>); never logged.</summary>
    public string? ApiKey { get; set; }

    public int MaxRetries { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// <see cref="IModelProvider"/> on the official Anthropic .NET SDK (stable 13.x, API version 2026-10-21). The SDK retries
/// 408/409/429/5xx and connection errors with backoff (<c>MaxRetries</c>); what still fails is mapped to
/// <see cref="ModelProviderException"/>. No tools are ever sent. Prompts and replies are never logged.
/// </summary>
public sealed class AnthropicModelProvider(AnthropicClient client) : IModelProvider
{
    public async Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct)
    {
        var system = new List<TextBlockParam> { new() { Text = request.System } };
        if (!string.IsNullOrWhiteSpace(request.CacheablePrefix))
            system.Add(new TextBlockParam { Text = request.CacheablePrefix });
        // One breakpoint at the end of the stable prefix: instructions (+ profile) are cached across calls.
        system[^1] = new TextBlockParam { Text = system[^1].Text, CacheControl = new CacheControlEphemeral() };

        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            System = system,
            Messages = request.Messages
                .Select(m => m.Role == ModelRole.User
                    ? (MessageParam)new UserMessageParam { Content = m.Text }
                    : new AssistantMessageParam { Content = m.Text })
                .ToList(),
            OutputConfig = BuildOutputConfig(request),
        };

        Message message;
        try
        {
            message = await client.Messages.Create(parameters, ct);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new ModelProviderException("Rate limited by the AI provider.", transient: true, 429, ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new ModelProviderException("The AI provider returned a server error or is overloaded.", transient: true, null, ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new ModelProviderException("Could not reach the AI provider.", transient: true, null, ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new ModelProviderException("The AI provider rejected the request.", transient: false, null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ModelProviderException("The AI provider timed out.", transient: true, null, ex);
        }

        var text = string.Concat(message.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        var u = message.Usage;
        var usage = new ModelUsage(
            (int)u.InputTokens,
            (int)u.OutputTokens,
            (int)(u.CacheCreationInputTokens ?? 0),
            (int)(u.CacheReadInputTokens ?? 0));
        return new ModelResponse(text, usage, message.Model.Raw() ?? request.Model, message.StopReason?.Raw());
    }

    private static OutputConfig? BuildOutputConfig(ModelRequest request)
    {
        if (request.JsonSchema is null && request.Effort is null) return null;
        var config = new OutputConfig();
        if (request.JsonSchema is not null)
        {
            var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.JsonSchema)
                ?? throw new InvalidOperationException("Empty JSON schema.");
            config = config with { Format = new JsonOutputFormat { Schema = schema } };
        }
        if (request.Effort is { } e)
            config = config with { Effort = ParseEffort(e) };
        return config;
    }

    private static Effort ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => throw new InvalidOperationException($"Unknown effort '{effort}'."),
    };
}
