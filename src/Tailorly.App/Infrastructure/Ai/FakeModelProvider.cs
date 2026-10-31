using System.Collections.Concurrent;
using System.Reflection;
using Tailorly.Core.Ports;

namespace Tailorly.App.Infrastructure.Ai;

/// <summary>
/// Offline model provider for tests and local dev (no API key). Returns a reply built from the request for the tailoring
/// tasks (<see cref="FakeReplies"/>), otherwise the embedded recorded fixture
/// <c>Fixtures/{task}.json</c>, or <c>{}</c>, with deterministic token usage (about 4 characters per token).
/// Tests can queue scripted replies or failures per task with <see cref="Enqueue"/> and inspect <see cref="Requests"/>.
/// </summary>
public sealed class FakeModelProvider : IModelProvider
{
    private static readonly Assembly Asm = typeof(FakeModelProvider).Assembly;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<ModelRequest, ModelResponse>>> _script = new();

    /// <summary>Every request received, in order (tests assert on prompts and parameters).</summary>
    public ConcurrentQueue<ModelRequest> Requests { get; } = new();

    /// <summary>Queues one reply (or a throwing function) for the next call of <paramref name="task"/>.</summary>
    public void Enqueue(string task, Func<ModelRequest, ModelResponse> reply) =>
        _script.GetOrAdd(task, _ => new()).Enqueue(reply);

    public void Enqueue(string task, string text, string stopReason = "end_turn") =>
        Enqueue(task, r => Reply(r, text, stopReason));

    public void ClearScript() => _script.Clear();

    public Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Enqueue(request);
        if (_script.TryGetValue(request.Task, out var q) && q.TryDequeue(out var scripted))
            return Task.FromResult(scripted(request));
        return Task.FromResult(Reply(request, FakeReplies.For(request) ?? LoadFixture(request.Task) ?? "{}", "end_turn"));
    }

    public static ModelResponse Reply(ModelRequest request, string text, string stopReason = "end_turn")
    {
        var input = Estimate(request.System) + Estimate(request.CacheablePrefix) + request.Messages.Sum(m => Estimate(m.Text));
        var usage = new ModelUsage(input, Math.Min(Estimate(text), request.MaxTokens), 0, 0);
        return new ModelResponse(text, usage, request.Model, stopReason);
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

    private static int Estimate(string? s) => string.IsNullOrEmpty(s) ? 0 : (s.Length + 3) / 4;
}
