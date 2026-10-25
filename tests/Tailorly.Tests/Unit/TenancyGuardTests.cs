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
        [typeof(AiUsage)] = "system ledger written by the AI runner; read only by admin endpoints (DatabaseDesign §9)",
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

    [GeneratedRegex(@"\b(ExecuteSql\w*|SqlQuery\w*|FromSql\w*)\s*(<[^>(]*>)?\s*\(")]
    private static partial Regex RawSqlCall();

    [GeneratedRegex(@"^\s*//.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();
}
