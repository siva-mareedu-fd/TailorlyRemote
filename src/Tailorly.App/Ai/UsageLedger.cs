using Microsoft.EntityFrameworkCore;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Ports;

namespace Tailorly.App.Ai;

/// <summary>
/// Writes one <c>ai_usage</c> row per model call (PRD §6.1): ids, model, token counts, latency, outcome. Never the
/// prompt or the reply. Written with SQL so it never flushes unrelated tracked changes of the caller's context.
/// </summary>
public sealed class UsageLedger(AppDbContext db)
{
    public Task WriteAsync(Guid? userId, string task, string model, string? promptVersion, string bucket, string periodKey,
        ModelUsage usage, int latencyMs, bool success, string? errorCode)
    {
        var counted = (int)Math.Min(int.MaxValue, usage.CountedTokens);
        // Not cancellable: a call that was made (and billed) is recorded even if the caller is shutting down.
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ai_usage (user_id, task, model, prompt_version, bucket, period_key, input_tokens, output_tokens,
                                  cache_create_tokens, cache_read_tokens, counted_tokens, latency_ms, success, error_code)
            VALUES ({userId}, {task}, {model}, {promptVersion}, {bucket}, {periodKey}, {usage.InputTokens}, {usage.OutputTokens},
                    {usage.CacheCreationTokens}, {usage.CacheReadTokens}, {counted}, {latencyMs}, {success}, {errorCode})
            """, CancellationToken.None);
    }
}
