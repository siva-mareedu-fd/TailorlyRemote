using System.Globalization;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Accounts;

public sealed class DeletionLogOptions
{
    /// <summary>Append-only file outside the database (a separate volume that the backup ships off-VM).</summary>
    public string Path { get; set; } = "deletion-log/deletions.log";
}

/// <summary>
/// The deletion log (SDD §13.4, S15): one line <c>{userId},{utc timestamp}</c> per confirmed deletion, appended outside
/// the database so that a restore from an older backup can be followed by replaying the purge for every listed id. No
/// email or other PII is written.
/// </summary>
public sealed class DeletionLog(IOptions<DeletionLogOptions> options)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string FilePath => System.IO.Path.GetFullPath(options.Value.Path);

    public async Task AppendAsync(Guid userId, DateTimeOffset at, CancellationToken ct)
    {
        var path = FilePath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var line = string.Create(CultureInfo.InvariantCulture, $"{userId:D},{at.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}\n");
        await Gate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(path, line, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>User ids listed in the log (the restore runbook replays the purge for each).</summary>
    public IReadOnlyList<Guid> ReadAll() => File.Exists(FilePath)
        ? File.ReadAllLines(FilePath)
            .Select(l => Guid.TryParse(l.Split(',')[0], out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList()
        : [];
}

/// <summary>Writes <c>audit_log</c> rows: ids and counts only, never PII or document text.</summary>
public sealed class AuditWriter(AppDbContext db)
{
    public Task WriteAsync(string action, Guid? actor, string? entity, string? entityId, object? meta, CancellationToken ct)
    {
        var json = meta is null ? null : JsonSerializer.Serialize(meta);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO audit_log (actor_user_id, action, entity, entity_id, meta)
            VALUES ({actor}, {action}, {entity}, {entityId}, CAST({json} AS jsonb))
            """, ct);
    }
}

/// <summary>
/// <c>PurgeDeletedAccounts</c> (SDD §7.5, DatabaseDesign §8): idempotent and resumable. Runs hourly for every account in
/// <c>pending_deletion</c>, and right after a deletion is confirmed. Deletes the user's files, writes an audit row
/// without PII, then deletes the Identity user, which cascades to every user-owned row.
/// </summary>
public sealed partial class PurgeDeletedAccountsJob(
    SystemQueries system,
    IFileStore files,
    AuditWriter audit,
    ILogger<PurgeDeletedAccountsJob> logger)
{
    public const string RecurringId = "purge-deleted-accounts";

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var id in await system.UsersPendingDeletionAsync(ct))
            await PurgeUserAsync(id, ct);
    }

    [AutomaticRetry(Attempts = 3)]
    public async Task PurgeUserAsync(Guid userId, CancellationToken ct)
    {
        var status = await system.AccountStatusAsync(userId, ct);
        if (status is null)
        {
            LogAlreadyGone(logger, userId);
            return;
        }
        if (status != AccountStatus.PendingDeletion) return; // never purge an account that was not confirmed

        var keys = await system.StorageKeysOfUserAsync(userId, ct);
        foreach (var key in keys)
            await files.DeleteAsync(key, ct);
        await files.DeletePrefixAsync($"{userId:D}/", ct);

        // Audit before the delete (DatabaseDesign §8): the id as text survives because actor_user_id is not set.
        await audit.WriteAsync("account.purged", actor: null, "user", userId.ToString("D"), new { files = keys.Count }, ct);
        await system.DeleteUserCascadeAsync(userId, ct);
        LogPurged(logger, userId, keys.Count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged account {UserId} ({FileCount} file(s))")]
    private static partial void LogPurged(ILogger logger, Guid userId, int fileCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Account {UserId} already purged")]
    private static partial void LogAlreadyGone(ILogger logger, Guid userId);
}
