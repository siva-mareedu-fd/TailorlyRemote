using System.Reflection;
using Tailorly.Core.Ports;

namespace Tailorly.App.Infrastructure.Ai;

/// <summary>
/// Offline model provider for tests and local dev (no API key). Returns the embedded fixture
/// <c>Fixtures/{task}.json</c> when present, otherwise <c>{}</c>, with deterministic token usage.
/// </summary>
public sealed class FakeModelProvider : IModelProvider
{
    private static readonly Assembly Asm = typeof(FakeModelProvider).Assembly;

    public Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct)
    {
        var text = LoadFixture(request.Task) ?? "{}";
        var input = EstimateTokens(request.System) + EstimateTokens(request.CacheablePrefix) + EstimateTokens(request.UserContent);
        var usage = new ModelUsage(input, Math.Min(EstimateTokens(text), request.MaxTokens), 0, 0);
        return Task.FromResult(new ModelResponse(text, usage, request.Model, "end_turn"));
    }

    private static string? LoadFixture(string task)
    {
        var name = Asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".Fixtures.{task}.json", StringComparison.Ordinal));
        if (name is null) return null;
        using var stream = Asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Roughly 4 characters per token; good enough for budget tests.
    private static int EstimateTokens(string? s) => string.IsNullOrEmpty(s) ? 0 : (s.Length + 3) / 4;
}
