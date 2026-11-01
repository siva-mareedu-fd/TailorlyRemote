using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Tailorly.App.Infrastructure.Data;

namespace Tailorly.Tests.Unit;

/// <summary>
/// Static guards for the tenancy rules the all-endpoints isolation test cannot see (SDD §14.1, DatabaseDesign §9):
/// every entity that carries a user id is filtered (or listed here with a reason), and only <see cref="SystemQueries"/>
/// bypasses the filter, whether with <c>IgnoreQueryFilters()</c> or with raw SQL on a user-owned table.
/// </summary>
public sealed partial class TenancyGuardTests
{
    /// <summary>Entities with a user id that are deliberately not user-filtered.</summary>
    private static readonly Dictionary<Type, string> UnfilteredWithUserId = new()
    {
        [typeof(AiUsage)] = "system ledger written by the AI runner; read by admin endpoints and, filtered by hand, GET /usage (ReviewedUnfilteredReads)",
        [typeof(AuditLog)] = "system audit trail; read only by admin endpoints (DatabaseDesign §9)",
        [typeof(AiTaskConfig)] = "shared model routing; updated_by is the admin who last edited it",
        [typeof(IdentityUserRole<Guid>)] = "Identity store; read only through UserManager for the caller's own id",
        [typeof(IdentityUserClaim<Guid>)] = "Identity store; read only through UserManager for the caller's own id",
        [typeof(IdentityUserLogin<Guid>)] = "Identity store; read only through UserManager for the caller's own id",
        [typeof(IdentityUserToken<Guid>)] = "Identity store; read only through UserManager for the caller's own id",
    };

    /// <summary>Raw SQL on a user-owned table outside <see cref="SystemQueries"/>: (file, table) → why it is safe.</summary>
    private static readonly Dictionary<(string File, string Table), string> ReviewedRawSql = new()
    {
        [("ResumeEndpoints.cs", "user_profiles")] =
            "résumé version counter: WHERE user_id = the caller's own id; UPDATE … RETURNING has no EF equivalent",
    };

    /// <summary>
    /// Reads of the unfiltered entities above outside admin features and <see cref="SystemQueries"/>: (file, entity) → how the
    /// file keeps other users' rows out. Nothing else does, so each one needs a test of its own.
    /// </summary>
    private static readonly Dictionary<(string File, string Entity), string> ReviewedUnfilteredReads = new()
    {
        [("UsageEndpoints.cs", nameof(AiUsage))] =
            "WHERE user_id = the caller's id on both queries; IsolationTests.Usage_counts_only_the_callers_tokens",
    };

    /// <summary>Hangfire entry points that take a user id but deliberately do not act as that user, with the reason.</summary>
    private static readonly Dictionary<string, string> JobsWithoutActAs = new()
    {
        ["PurgeUserAsync"] = "deletes across the user's rows and files through SystemQueries, after checking pending_deletion",
    };

    private static IModel Model()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        return db.Model;
    }

    [Fact]
    public void Every_user_owned_entity_has_the_user_filter()
    {
        var owned = Model().GetEntityTypes().Where(e => typeof(IUserOwned).IsAssignableFrom(e.ClrType)).ToList();
        owned.Select(e => e.ClrType).Should().Contain([typeof(UserProfile), typeof(Resume), typeof(ResumeProfile),
            typeof(UserConfirmedSkill), typeof(UserBudgetCounter), typeof(UserPreferences), typeof(UserWatchedCompany)]);

        foreach (var e in owned)
            e.GetDeclaredQueryFilters().Should().ContainSingle(f => f.Expression!.ToString().Contains("UserId", StringComparison.Ordinal),
                $"{e.ClrType.Name} is user-owned and must be filtered on UserId");
    }

    [Fact]
    public void Every_entity_with_a_user_id_is_user_owned_or_reviewed()
    {
        var model = Model();
        var userEntity = model.FindEntityType(typeof(AppUser))!;
        var unreviewed = model.GetEntityTypes()
            .Where(e => e != userEntity && !typeof(IUserOwned).IsAssignableFrom(e.ClrType) && !UnfilteredWithUserId.ContainsKey(e.ClrType))
            .Where(e => e.GetProperties().Any(p => p.Name.EndsWith("UserId", StringComparison.Ordinal))
                        || e.GetForeignKeys().Any(fk => fk.PrincipalEntityType == userEntity))
            .Select(e => e.ClrType.Name)
            .ToList();

        string.Join(", ", unreviewed).Should().BeEmpty("an entity that references a user must implement IUserOwned (and so get the filter), " +
                                    "or be listed in UnfilteredWithUserId with the reason it is safe");
    }

    [Fact]
    public void Only_SystemQueries_calls_IgnoreQueryFilters()
    {
        var offenders = SourceFiles()
            .Where(f => Path.GetFileName(f) != "SystemQueries.cs")
            .Where(f => IgnoreFilters().IsMatch(StripComments(File.ReadAllText(f))))
            .Select(Relative)
            .ToList();

        string.Join(", ", offenders).Should().BeEmpty("IgnoreQueryFilters() belongs only in SystemQueries (DatabaseDesign §9)");
    }

    [Fact]
    public void Raw_sql_on_user_owned_tables_lives_only_in_SystemQueries()
    {
        var tables = Model().GetEntityTypes()
            .Where(e => typeof(IUserOwned).IsAssignableFrom(e.ClrType))
            .Select(e => e.GetTableName()!)
            .ToList();
        tables.Should().Contain("resumes");

        var found = new List<(string File, string Table)>();
        foreach (var file in SourceFiles().Where(f => Path.GetFileName(f) != "SystemQueries.cs"))
        {
            var code = StripComments(File.ReadAllText(file));
            foreach (Match call in RawSqlCall().Matches(code))
            {
                var sql = FirstStringLiteral(code, call.Index + call.Length);
                foreach (var t in tables.Where(t => Regex.IsMatch(sql, $@"\b{t}\b", RegexOptions.IgnoreCase)))
                    found.Add((Path.GetFileName(file), t));
            }
        }

        string.Join(", ", found.Distinct().Where(x => !ReviewedRawSql.ContainsKey(x)).Select(x => $"{x.File}: {x.Table}"))
            .Should().BeEmpty("raw SQL skips the query filter; put it in SystemQueries or review it in ReviewedRawSql");
        ReviewedRawSql.Keys.Should().OnlyContain(k => found.Contains(k), "the reviewed list must not name SQL that no longer exists");
    }

    [Fact]
    public void Every_background_job_that_takes_a_user_id_acts_as_that_user()
    {
        var jobs = new List<(string File, string Method, bool ActsAs)>();
        foreach (var file in SourceFiles())
        {
            var code = StripComments(File.ReadAllText(file));
            foreach (Match m in UserJob().Matches(code))
            {
                if (!m.Groups["args"].Value.Contains("Guid userId", StringComparison.Ordinal)) continue;
                var end = code.IndexOf("\n    }", m.Index + m.Length, StringComparison.Ordinal); // the method's closing brace
                var body = code[(m.Index + m.Length)..(end < 0 ? code.Length : end)];
                jobs.Add((Path.GetFileName(file), m.Groups["name"].Value, body.Contains("CurrentUser.ActAs(", StringComparison.Ordinal)));
            }
        }

        jobs.Select(j => j.Method).Should().Contain(["RunAsync", "SendAsync", "PurgeUserAsync"], "the scan must see the job classes");
        jobs.Count(j => j.Method == "RunAsync").Should().BeGreaterThanOrEqualTo(2, "ParseResumeJob and PerUserRunJob");
        string.Join(", ", jobs.Where(j => !j.ActsAs && !JobsWithoutActAs.ContainsKey(j.Method)).Select(j => $"{j.File}: {j.Method}"))
            .Should().BeEmpty("a job that works for one user must call CurrentUser.ActAs(userId) first so the query filters apply");
        JobsWithoutActAs.Keys.Should().OnlyContain(k => jobs.Any(j => j.Method == k && !j.ActsAs), "the list must not name jobs that act as the user");
    }

    [Fact]
    public void Unfiltered_user_tables_are_read_only_by_admin_features_or_reviewed_code()
    {
        var found = new List<(string File, string Entity)>();
        foreach (var file in SourceFiles().Where(f => Path.GetFileName(f) is not ("SystemQueries.cs" or "AppDbContext.cs")
                                                     && !Relative(f).Contains($"Features{Path.DirectorySeparatorChar}Admin", StringComparison.Ordinal)))
            foreach (Match m in UnfilteredRead().Matches(StripComments(File.ReadAllText(file))))
                found.Add((Path.GetFileName(file), m.Groups["entity"].Value));

        string.Join(", ", found.Distinct().Where(x => !ReviewedUnfilteredReads.ContainsKey(x)).Select(x => $"{x.File}: {x.Entity}"))
            .Should().BeEmpty("these tables have no query filter: read them in an admin feature or SystemQueries, or review the read here");
        ReviewedUnfilteredReads.Keys.Should().OnlyContain(k => found.Contains(k), "the reviewed list must not name reads that no longer exist");
    }

    [Fact]
    public void Every_enqueued_job_that_takes_a_user_id_is_seen_by_the_ActAs_scan()
    {
        // Job entry points are what the code enqueues, schedules or registers as recurring; the ActAs scan below finds
        // them by [AutomaticRetry], so a user job without that attribute would slip past it.
        var assembly = typeof(AppDbContext).Assembly;
        var entryPoints = SourceFiles()
            .SelectMany(f => JobReference().Matches(StripComments(File.ReadAllText(f))))
            .Select(m => (Type: m.Groups["type"].Value, Method: m.Groups["method"].Value))
            .Distinct()
            .ToList();
        entryPoints.Should().Contain([("ParseResumeJob", "RunAsync"), ("PurgeDeletedAccountsJob", "PurgeUserAsync"),
            ("SweepStuckWorkJob", "RunAsync")], "the scan must see enqueued and recurring jobs");

        var unseen = new List<string>();
        foreach (var (typeName, methodName) in entryPoints)
        {
            var methods = assembly.GetTypes().Where(t => t.Name == typeName)
                .SelectMany(t => t.GetMethods().Where(m => m.Name == methodName)).ToList();
            methods.Should().NotBeEmpty($"{typeName}.{methodName} is enqueued, so it must exist");
            unseen.AddRange(methods
                .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(Guid) && p.Name == "userId"))
                .Where(m => !m.IsDefined(typeof(Hangfire.AutomaticRetryAttribute), inherit: false))
                .Select(m => $"{typeName}.{methodName}"));
        }
        string.Join(", ", unseen).Should().BeEmpty("a job that takes a user id must declare [AutomaticRetry] so the ActAs scan checks it");
    }

    [Fact]
    public void Guards_see_the_source_tree()
    {
        var files = SourceFiles().Select(Path.GetFileName).ToList();
        files.Should().Contain(["SystemQueries.cs", "ResumeEndpoints.cs", "BudgetService.cs"]);
        files.Should().NotContain(f => f!.EndsWith(".Designer.cs", StringComparison.Ordinal), "migrations are not scanned");
    }

    // ---------- source helpers ----------

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Tailorly.sln"))) return dir.FullName;
        throw new InvalidOperationException("Tailorly.sln not found above the test output directory.");
    }

    private static string Relative(string path) => Path.GetRelativePath(RepoRoot(), path);

    /// <summary>Production C# under <c>src/</c>, without build output and generated migrations.</summary>
    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Relative(f).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or "Migrations"));

    /// <summary>Removes <c>//</c> line comments (including XML docs) so documentation that names the API is not a hit.</summary>
    private static string StripComments(string code) => LineComment().Replace(code, "");

    /// <summary>The first C# string literal at or after <paramref name="start"/>: raw (<c>"""…"""</c>) or regular.</summary>
    private static string FirstStringLiteral(string code, int start)
    {
        var open = code.IndexOf('"', start);
        if (open < 0) return "";
        if (string.CompareOrdinal(code, open, "\"\"\"", 0, 3) == 0)
        {
            var close = code.IndexOf("\"\"\"", open + 3, StringComparison.Ordinal);
            return close < 0 ? code[open..] : code[(open + 3)..close];
        }
        var end = open + 1;
        while (end < code.Length && (code[end] != '"' || code[end - 1] == '\\')) end++;
        return code[(open + 1)..Math.Min(end, code.Length)];
    }

    [GeneratedRegex(@"\.IgnoreQueryFilters\s*\(")]
    private static partial Regex IgnoreFilters();

    /// <summary>EF raw SQL, or an ADO command built by hand (its SQL is the next string literal, or the one assigned to CommandText).</summary>
    [GeneratedRegex(@"\b(ExecuteSql\w*|SqlQuery\w*|FromSql\w*)\s*(<[^>(]*>)?\s*\(|\bnew\s+NpgsqlCommand\s*\(|\bCommandText\s*=")]
    private static partial Regex RawSqlCall();

    /// <summary>An <c>[AutomaticRetry]</c> job method: its name and parameter list (the body follows).</summary>
    [GeneratedRegex(@"\[AutomaticRetry[^\n]*\]\s*(?:\[[^\n]*\]\s*)*public\s+async\s+Task\w*(?:<[^>]*>)?\s+(?<name>\w+)\((?<args>[^)]*)\)")]
    private static partial Regex UserJob();

    /// <summary>A read of an unfiltered entity that references users, through its DbSet or <c>Set&lt;T&gt;()</c>.</summary>
    [GeneratedRegex(@"\b(?:db\.(?<entity>AiUsage|AuditLog)\b|Set<(?<entity>AiUsage|AuditLog)>\s*\()")]
    private static partial Regex UnfilteredRead();

    /// <summary>A Hangfire job reference: <c>Enqueue&lt;T&gt;(j =&gt; j.M(…))</c>, <c>Schedule&lt;T&gt;</c> or <c>AddOrUpdate&lt;T&gt;(id, j =&gt; j.M(…))</c>.</summary>
    [GeneratedRegex(@"\b(?:Enqueue|Schedule|AddOrUpdate)<(?<type>\w+)>\([^;]*?=>\s*\w+\.(?<method>\w+)\(")]
    private static partial Regex JobReference();

    [GeneratedRegex(@"^\s*//.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();
}
