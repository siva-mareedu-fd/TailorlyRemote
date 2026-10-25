using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tailorly.App.Infrastructure.Data;

namespace Tailorly.Tests.Infrastructure;

/// <summary>Direct database access for arranging and asserting state in integration tests.</summary>
public static class Db
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<T> WithContextAsync<T>(TailorlyAppFactory f, Guid actAs, Func<AppDbContext, Task<T>> work)
    {
        await using var scope = f.Services.CreateAsyncScope();
        using var _ = CurrentUser.ActAs(actAs);
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static async Task<int> ExecAsync(string connectionString, string sql, params (string Name, object? Value)[] args)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(Ct);
    }

    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql, params (string Name, object? Value)[] args)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = await cmd.ExecuteScalarAsync(Ct);
        return r is null or DBNull ? default : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<List<string>> StringsAsync(string connectionString, string sql, params (string Name, object? Value)[] args)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        var list = new List<string>();
        while (await r.ReadAsync(Ct)) list.Add(r.IsDBNull(0) ? "" : r.GetValue(0).ToString()!);
        return list;
    }

    /// <summary>Sets a user's usage in the current IST period (creating the counter row).</summary>
    public static Task SetUsedAsync(string cs, Guid userId, string bucket, long used, long limit) => ExecAsync(cs, """
        INSERT INTO user_budget_counters (user_id, period_key, bucket, limit_tokens, used_tokens)
        VALUES (@u, to_char(now() AT TIME ZONE 'Asia/Kolkata', 'YYYY-MM'), @b, @l, @used)
        ON CONFLICT (user_id, period_key, bucket) DO UPDATE SET used_tokens = EXCLUDED.used_tokens, limit_tokens = EXCLUDED.limit_tokens
        """, ("u", userId), ("b", bucket), ("l", limit), ("used", used));
}
