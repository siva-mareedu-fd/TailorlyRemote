using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Tailorly.App.Infrastructure.Data;

// Mirrors DatabaseDesign §3.1, §3.2 and §3.7. Names are snake_case via EFCore.NamingConventions.

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
        e.Property(x => x.ResumeVersionsIssued).HasDefaultValue(0);
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

internal sealed class ResumeConfig : IEntityTypeConfiguration<Resume>
{
    public void Configure(EntityTypeBuilder<Resume> e)
    {
        e.ToTable("resumes", t => t.HasCheckConstraint("ck_resumes_parse_status",
            "parse_status IN ('queued','running','ready','failed')"));
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.IsActive).HasDefaultValue(false);
        e.Property(x => x.ParseStatus).HasDefaultValue(WorkStatus.Queued);
        e.Property(x => x.FormatFindings).HasColumnType("jsonb");
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.UserId, x.Version }).IsUnique().HasDatabaseName("ux_resumes_user_version");
        // Two partial unique indexes on the same column must be named, or EF merges them into one.
        e.HasIndex(x => x.UserId, "ux_resumes_one_active").IsUnique().HasFilter("is_active")
            .HasDatabaseName("ux_resumes_one_active");
        // One parse in flight per user: a repeated upload while one is parsing returns the existing résumé (ApiDesign §1).
        e.HasIndex(x => x.UserId, "ux_resumes_parse_inflight").IsUnique().HasFilter("parse_status IN ('queued','running')")
            .HasDatabaseName("ux_resumes_parse_inflight");
    }
}

internal sealed class ResumeProfileConfig : IEntityTypeConfiguration<ResumeProfile>
{
    public void Configure(EntityTypeBuilder<ResumeProfile> e)
    {
        e.ToTable("resume_profiles");
        e.HasKey(x => x.ResumeId);
        e.Property(x => x.ResumeId).ValueGeneratedNever();
        e.Property(x => x.Parsed).HasColumnType("jsonb");
        e.Property(x => x.UserEdited).HasColumnType("jsonb");
        e.Property(x => x.SchemaVersion).HasDefaultValue(1);
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        e.HasOne<Resume>().WithOne().HasForeignKey<ResumeProfile>(x => x.ResumeId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserConfirmedSkillConfig : IEntityTypeConfiguration<UserConfirmedSkill>
{
    public void Configure(EntityTypeBuilder<UserConfirmedSkill> e)
    {
        e.ToTable("user_confirmed_skills");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.UserId, x.SkillKey }).IsUnique().HasDatabaseName("ux_user_confirmed_skills_key");
    }
}

internal sealed class UserBudgetCounterConfig : IEntityTypeConfiguration<UserBudgetCounter>
{
    public void Configure(EntityTypeBuilder<UserBudgetCounter> e)
    {
        e.ToTable("user_budget_counters", t =>
        {
            t.HasCheckConstraint("ck_user_budget_counters_bucket", "bucket IN ('discovery','documents')");
            t.HasCheckConstraint("ck_user_budget_counters_limit", "limit_tokens >= 0");
            t.HasCheckConstraint("ck_user_budget_counters_used", "used_tokens >= 0");
        });
        e.HasKey(x => new { x.UserId, x.PeriodKey, x.Bucket });
        e.Property(x => x.UsedTokens).HasDefaultValue(0L);
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AiUsageConfig : IEntityTypeConfiguration<AiUsage>
{
    public void Configure(EntityTypeBuilder<AiUsage> e)
    {
        e.ToTable("ai_usage");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.Property(x => x.InputTokens).HasDefaultValue(0);
        e.Property(x => x.OutputTokens).HasDefaultValue(0);
        e.Property(x => x.CacheCreateTokens).HasDefaultValue(0);
        e.Property(x => x.CacheReadTokens).HasDefaultValue(0);
        e.Property(x => x.CountedTokens).HasDefaultValue(0);
        e.Property(x => x.EstCostUsd).HasPrecision(10, 5);
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        e.HasIndex(x => new { x.UserId, x.PeriodKey, x.Bucket }).HasDatabaseName("ix_ai_usage_user_period");
        e.HasIndex(x => x.CreatedAt).HasFilter("user_id IS NULL").HasDatabaseName("ix_ai_usage_platform_day");
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
