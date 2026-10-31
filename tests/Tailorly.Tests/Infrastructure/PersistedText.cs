using Npgsql;

namespace Tailorly.Tests.Infrastructure;

/// <summary>
/// Reads tables that must never hold résumé or job text, prompts, replies, emails or tokens, one row per line as JSON
/// (every column, so a new column is scanned too): <c>audit_log</c>, <c>ai_usage</c> and Hangfire's job tables, which
/// store a failed job's exception message and details.
/// </summary>
public static class PersistedText
{
    public static Task<List<string>> AuditLogAsync(string connectionString) =>
        RowsAsync(connectionString, "SELECT row_to_json(a)::text FROM audit_log a");

    public static Task<List<string>> AiUsageAsync(string connectionString) =>
        RowsAsync(connectionString, "SELECT row_to_json(u)::text FROM ai_usage u");

    /// <summary>The job row, its parameters and every state it went through (reason and data).</summary>
    public static async Task<List<string>> HangfireJobAsync(string connectionString, long jobId)
    {
        var rows = new List<string>();
        foreach (var sql in new[]
                 {
                     "SELECT row_to_json(j)::text FROM hangfire.job j WHERE j.id = @id",
                     "SELECT row_to_json(p)::text FROM hangfire.jobparameter p WHERE p.jobid = @id",
                     "SELECT row_to_json(s)::text FROM hangfire.state s WHERE s.jobid = @id",
                 })
            rows.AddRange(await RowsAsync(connectionString, sql, jobId));
        return rows;
    }

    private static async Task<List<string>> RowsAsync(string connectionString, string sql, long? id = null)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (id is not null) cmd.Parameters.AddWithValue("id", id.Value);
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }
}
