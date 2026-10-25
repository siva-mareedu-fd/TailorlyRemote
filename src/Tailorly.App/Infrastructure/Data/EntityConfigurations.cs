using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Tailorly.App.Infrastructure.Data;

// Mirrors DatabaseDesign §3.1 and the ai_task_config table of §3.7. Names are snake_case via EFCore.NamingConventions.

internal sealed class UserProfileConfig : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> e)
    {
        e.ToTable("user_profiles", t => t.HasCheckConstraint("ck_user_profiles_account_status",
            "account_status IN ('active','pending_deletion','suspended')"));
        e.HasKey(x => x.UserId);
        e.Property(x => x.UserId).ValueGeneratedNever();
        e.Property(x => x.TimeZone).HasDefaultValue("Asia/Kolkata");
        e.Property(x => x.AccountStatus).HasDefaultValue(AccountStatus.Active);
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        e.HasOne<AppUser>().WithOne().HasForeignKey<UserProfile>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class InviteCodeConfig : IEntityTypeConfiguration<InviteCode>
{
    public void Configure(EntityTypeBuilder<InviteCode> e)
    {
        e.ToTable("invite_codes", t =>
        {
            t.HasCheckConstraint("ck_invite_codes_max_uses", "max_uses > 0");
            t.HasCheckConstraint("ck_invite_codes_used_count", "used_count >= 0");
        });
        e.HasKey(x => x.Code);
        e.Property(x => x.MaxUses).HasDefaultValue(1);
        e.Property(x => x.UsedCount).HasDefaultValue(0);
    }
}

internal sealed class FeatureFlagConfig : IEntityTypeConfiguration<FeatureFlag>
{
    public void Configure(EntityTypeBuilder<FeatureFlag> e)
    {
        e.ToTable("feature_flags");
        e.HasKey(x => x.Key);
        e.Property(x => x.Value).HasColumnType("jsonb");
    }
}

internal sealed class AuditLogConfig : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> e)
    {
        e.ToTable("audit_log");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.Property(x => x.At).HasDefaultValueSql("now()");
        e.Property(x => x.Meta).HasColumnType("jsonb");
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.SetNull);
        e.HasIndex(x => x.At).IsDescending().HasDatabaseName("ix_audit_log_at");
    }
}

internal sealed class AiTaskConfigConfig : IEntityTypeConfiguration<AiTaskConfig>
{
    public void Configure(EntityTypeBuilder<AiTaskConfig> e)
    {
        e.ToTable("ai_task_config", t =>
        {
            t.HasCheckConstraint("ck_ai_task_config_max_tokens", "max_tokens > 0");
            t.HasCheckConstraint("ck_ai_task_config_temperature", "temperature BETWEEN 0 AND 1");
            t.HasCheckConstraint("ck_ai_task_config_bucket", "bucket IN ('discovery','documents','platform')");
        });
        e.HasKey(x => x.Task);
        e.Property(x => x.Temperature).HasPrecision(3, 2).HasDefaultValue(0.2m);
        e.Property(x => x.Enabled).HasDefaultValue(true);
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
    }
}
