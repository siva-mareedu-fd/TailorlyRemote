using System.Reflection;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Tailorly.App.Infrastructure.Data;

/// <summary>
/// The single EF Core context. Every <see cref="IUserOwned"/> entity gets a global query filter on the
/// current user, which is <see cref="Guid.Empty"/> when unauthenticated, so queries fail closed.
///
/// IgnoreQueryFilters() is allowed ONLY in <see cref="SystemQueries"/> (digest dispatcher, purge job,
/// stuck-work sweep, budget service, admin queries). Review every use there.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    /// <summary>Read by the query filters at query time (EF parameterises context members).</summary>
    private Guid CurrentUserId => currentUser.UserId;

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<InviteCode> InviteCodes => Set<InviteCode>();
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();
    public DbSet<AuditLog> AuditLog => Set<AuditLog>();
    public DbSet<AiTaskConfig> AiTaskConfig => Set<AiTaskConfig>();
    public DbSet<Resume> Resumes => Set<Resume>();
    public DbSet<ResumeProfile> ResumeProfiles => Set<ResumeProfile>();
    public DbSet<UserConfirmedSkill> UserConfirmedSkills => Set<UserConfirmedSkill>();
    public DbSet<UserBudgetCounter> UserBudgetCounters => Set<UserBudgetCounter>();
    public DbSet<AiUsage> AiUsage => Set<AiUsage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Identity sets explicit PascalCase table/index names, which the snake_case convention does not override.
        foreach (var entity in b.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is not null && table.StartsWith("AspNet", StringComparison.Ordinal))
                entity.SetTableName(ToSnake(table));
            foreach (var index in entity.GetIndexes())
                if (index.GetDatabaseName() is { } n && !n.Contains('_'))
                    index.SetDatabaseName("ix_" + ToSnake(table!) + "_" + ToSnake(n.Replace("Index", "", StringComparison.Ordinal)));
        }

        b.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        var apply = typeof(AppDbContext).GetMethod(nameof(ApplyUserFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var type in b.Model.GetEntityTypes().Select(e => e.ClrType).Where(t => typeof(IUserOwned).IsAssignableFrom(t)))
            apply.MakeGenericMethod(type).Invoke(this, [b]);
    }

    private static string ToSnake(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && !char.IsUpper(name[i - 1])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private void ApplyUserFilter<T>(ModelBuilder b) where T : class, IUserOwned =>
        b.Entity<T>().HasQueryFilter(e => e.UserId == CurrentUserId);
}
