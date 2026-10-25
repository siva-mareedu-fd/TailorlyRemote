using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Tailorly.App.Infrastructure.Data;

namespace Tailorly.App.Ai;

/// <summary>Model routing per task from <c>ai_task_config</c>, cached for 60 s (SDD §9.3). Model ids live only there.</summary>
public sealed class TaskConfigCache(IMemoryCache cache, IServiceScopeFactory scopes)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public async Task<AiTaskConfig?> GetAsync(string task, CancellationToken ct) =>
        await cache.GetOrCreateAsync(Key(task), async e =>
        {
            e.AbsoluteExpirationRelativeToNow = Ttl;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.AiTaskConfig.AsNoTracking().FirstOrDefaultAsync(c => c.Task == task, ct);
        });

    /// <summary>Called after an admin edits a task (M6) so the change applies immediately.</summary>
    public void Invalidate(string task) => cache.Remove(Key(task));

    private static string Key(string task) => "ai_task_config:" + task;
}
