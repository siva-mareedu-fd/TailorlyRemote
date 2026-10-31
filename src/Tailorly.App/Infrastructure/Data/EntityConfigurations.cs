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

// DatabaseDesign §3.3

internal sealed class UserPreferencesConfig : IEntityTypeConfiguration<UserPreferences>
{
    public void Configure(EntityTypeBuilder<UserPreferences> e)
    {
        e.ToTable("user_preferences", t =>
        {
            t.HasCheckConstraint("ck_user_preferences_search_scope",
                "search_scope IN ('india_only','india_plus_global_remote','global_remote_only')");
            t.HasCheckConstraint("ck_user_preferences_salary_min", "salary_min IS NULL OR salary_min >= 0");
            t.HasCheckConstraint("ck_user_preferences_salary_max", "salary_max IS NULL OR salary_max >= 0");
            t.HasCheckConstraint("ck_user_preferences_salary_currency", "salary_currency IN ('INR','USD')");
            t.HasCheckConstraint("ck_user_preferences_min_match_score", "min_match_score BETWEEN 1 AND 10");
            t.HasCheckConstraint("ck_user_preferences_salary_range",
                "salary_max IS NULL OR salary_min IS NULL OR salary_max >= salary_min");
        });
        e.HasKey(x => x.UserId);
        e.Property(x => x.UserId).ValueGeneratedNever();
        e.Property(x => x.SearchScope).HasDefaultValue(SearchScopes.IndiaPlusGlobalRemote);
        e.Property(x => x.TargetRoles).HasDefaultValueSql("'{}'");
        e.Property(x => x.Locations).HasDefaultValueSql("'{}'");
        e.Property(x => x.WorkModes).HasDefaultValueSql("'{remote}'");
        e.Property(x => x.JobTypes).HasDefaultValueSql("'{full_time}'");
        e.Property(x => x.HiringModels).HasDefaultValueSql("'{employee,contractor}'");
        e.Property(x => x.ExcludedCompanies).HasDefaultValueSql("'{}'");
        e.Property(x => x.ExcludedKeywords).HasDefaultValueSql("'{}'");
        e.Property(x => x.SalaryCurrency).HasDefaultValue("INR");
        e.Property(x => x.MinMatchScore).HasDefaultValue((short)8);
        e.Property(x => x.IncludeUnspecifiedSalary).HasDefaultValue(true);
        e.Property(x => x.ShowUnclearEligibility).HasDefaultValue(false);
        e.Property(x => x.ScheduleEnabled).HasDefaultValue(true);
        e.Property(x => x.ScheduleLocalTime).HasDefaultValueSql("'18:00'");
        e.Property(x => x.DigestWhenEmpty).HasDefaultValue(false);
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.Version).IsRowVersion();
        e.HasOne<AppUser>().WithOne().HasForeignKey<UserPreferences>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserWatchedCompanyConfig : IEntityTypeConfiguration<UserWatchedCompany>
{
    public void Configure(EntityTypeBuilder<UserWatchedCompany> e)
    {
        e.ToTable("user_watched_companies");
        e.HasKey(x => new { x.UserId, x.CompanyAtsLinkId });
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<CompanyAtsLink>().WithMany().HasForeignKey(x => x.CompanyAtsLinkId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_watched_link");
    }
}

// DatabaseDesign §3.4

internal sealed class JobSourceConfig : IEntityTypeConfiguration<JobSource>
{
    public void Configure(EntityTypeBuilder<JobSource> e)
    {
        e.ToTable("job_sources", t =>
        {
            t.HasCheckConstraint("ck_job_sources_kind", "kind IN ('feed','board','user')");
            t.HasCheckConstraint("ck_job_sources_poll_interval", "poll_interval_minutes > 0");
        });
        e.HasKey(x => x.Key);
        e.Property(x => x.Enabled).HasDefaultValue(true);
        e.Property(x => x.RequiresAttribution).HasDefaultValue(false);
        e.Property(x => x.RequireSourceApplyUrl).HasDefaultValue(false);
        e.Property(x => x.ForbidThirdPartyResolution).HasDefaultValue(false);
        e.Property(x => x.PurgeOnExpiry).HasDefaultValue(false);
        e.Property(x => x.RequiresLogoAttribution).HasDefaultValue(false);
        e.Property(x => x.PollIntervalMinutes).HasDefaultValue(360);
    }
}

internal sealed class SourceHealthConfig : IEntityTypeConfiguration<SourceHealth>
{
    public void Configure(EntityTypeBuilder<SourceHealth> e)
    {
        e.ToTable("source_health");
        e.HasKey(x => x.SourceKey);
        e.Property(x => x.ConsecutiveFailures).HasDefaultValue(0);
        e.Property(x => x.CallsThisMonth).HasDefaultValue(0);
        e.Property(x => x.LastRunStats).HasColumnType("jsonb");
        e.HasOne<JobSource>().WithOne().HasForeignKey<SourceHealth>(x => x.SourceKey).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CompanyConfig : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> e)
    {
        e.ToTable("companies");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasIndex(x => x.NormalizedName).IsUnique().HasDatabaseName("ux_companies_normalized");
        e.HasIndex(x => x.WebsiteDomain).HasFilter("website_domain IS NOT NULL").HasDatabaseName("ix_companies_domain");
    }
}

internal sealed class CompanyAtsLinkConfig : IEntityTypeConfiguration<CompanyAtsLink>
{
    public void Configure(EntityTypeBuilder<CompanyAtsLink> e)
    {
        e.ToTable("company_ats_links", t =>
        {
            t.HasCheckConstraint("ck_company_ats_links_platform", "platform IN ('greenhouse','lever','ashby')");
            t.HasCheckConstraint("ck_company_ats_links_board_token", "board_token ~ '^[a-z0-9_-]{2,60}$'");
            t.HasCheckConstraint("ck_company_ats_links_status", "status IN ('resolved','not_found','blocked')");
            t.HasCheckConstraint("ck_company_ats_links_origin", "origin IN ('seed','user','discovered','guessed','admin')");
            t.HasCheckConstraint("ck_company_ats_links_confidence", "confidence IN ('high','medium','low')");
        });
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.Status).HasDefaultValue(LinkStatus.Resolved);
        e.Property(x => x.Confidence).HasDefaultValue("high");
        e.Property(x => x.FailureCount).HasDefaultValue(0);
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.Platform, x.BoardToken }).IsUnique().HasDatabaseName("ux_company_ats_links_platform_token");
        e.HasIndex(x => x.CompanyId).HasDatabaseName("ix_company_ats_links_company");
        e.HasIndex(x => new { x.Status, x.NextRetryAt }).HasDatabaseName("ix_company_ats_links_poll");
    }
}

internal sealed class JobConfig : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> e)
    {
        e.ToTable("jobs", t =>
        {
            t.HasCheckConstraint("ck_jobs_work_mode", "work_mode IN ('remote','hybrid','onsite')");
            t.HasCheckConstraint("ck_jobs_employment_type", "employment_type IN ('full_time','contract','part_time','internship')");
            t.HasCheckConstraint("ck_jobs_hiring_model_hint", "hiring_model_hint IN ('employee','contractor','unknown')");
            t.HasCheckConstraint("ck_jobs_salary_kind", "salary_kind IN ('stated','estimated','unknown')");
            t.HasCheckConstraint("ck_jobs_description_completeness", "description_completeness IN ('full','limited')");
        });
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.LocationClass).HasDefaultValue("unknown");
        e.Property(x => x.SalaryCurrency).HasColumnType("char(3)");
        e.Property(x => x.SalaryKind).HasDefaultValue("unknown");
        e.Property(x => x.DescriptionCompleteness).HasDefaultValue(Completeness.Limited);
        e.Property(x => x.SourceMeta).HasColumnType("jsonb");
        e.Property(x => x.FetchedAt).HasDefaultValueSql("now()");
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasOne<JobSource>().WithMany().HasForeignKey(x => x.SourceKey).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Job>().WithMany().HasForeignKey(x => x.DuplicateOfJobId).OnDelete(DeleteBehavior.SetNull);
        e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
        e.HasIndex(x => new { x.SourceKey, x.ExternalId }).IsUnique().HasDatabaseName("ux_jobs_source_external");
        e.HasIndex(x => x.DedupeKey).HasDatabaseName("ix_jobs_dedupe");
        e.HasIndex(x => x.CreatedAt).IsDescending().HasFilter("duplicate_of_job_id IS NULL AND closed_at IS NULL")
            .HasDatabaseName("ix_jobs_created");
        e.HasIndex(x => x.ExpiresAt).HasFilter("expires_at IS NOT NULL").HasDatabaseName("ix_jobs_expires");
        // search_tsv (a generated tsvector) and its GIN index are created by raw SQL in the migration (DatabaseDesign §9).
    }
}


internal sealed class JobEnrichmentConfig : IEntityTypeConfiguration<JobEnrichment>
{
    public void Configure(EntityTypeBuilder<JobEnrichment> e)
    {
        e.ToTable("job_enrichment", t => t.HasCheckConstraint("ck_job_enrichment_eligibility",
            "eligibility IN ('worldwide','india_eligible','restricted','unclear')"));
        e.HasKey(x => x.JobId);
        e.Property(x => x.JobId).ValueGeneratedNever();
        e.Property(x => x.Data).HasColumnType("jsonb");
        e.Property(x => x.SchemaVersion).HasDefaultValue(1);
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasOne<Job>().WithOne().HasForeignKey<JobEnrichment>(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.Eligibility).HasDatabaseName("ix_job_enrichment_eligibility");
    }
}

// DatabaseDesign §3.5

internal sealed class UserJobScoreConfig : IEntityTypeConfiguration<UserJobScore>
{
    public void Configure(EntityTypeBuilder<UserJobScore> e)
    {
        e.ToTable("user_job_scores", t =>
        {
            t.HasCheckConstraint("ck_user_job_scores_fit_score", "fit_score BETWEEN 1 AND 10");
            t.HasCheckConstraint("ck_user_job_scores_confidence", "confidence IN ('high','medium','low')");
        });
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.Reasons).HasColumnType("jsonb");
        e.Property(x => x.MatchedSkills).HasColumnType("jsonb").HasDefaultValueSql("'[]'");
        e.Property(x => x.MissingSkills).HasColumnType("jsonb").HasDefaultValueSql("'[]'");
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.UserId, x.JobId, x.ResumeVersion }).IsUnique().HasDatabaseName("ux_user_job_scores_user_job_version");
        e.HasIndex(x => new { x.UserId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_scores_user_created");
        e.Ignore(x => x.IsRuleBased);
    }
}

internal sealed class DigestConfig : IEntityTypeConfiguration<Digest>
{
    public void Configure(EntityTypeBuilder<Digest> e)
    {
        e.ToTable("digests", t =>
        {
            t.HasCheckConstraint("ck_digests_kind", "kind IN ('scheduled','manual')");
            t.HasCheckConstraint("ck_digests_status", "status IN ('queued','running','ready','failed')");
        });
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.Status).HasDefaultValue(WorkStatus.Queued);
        e.Property(x => x.Attempt).HasDefaultValue(1);
        e.Property(x => x.Degraded).HasDefaultValue(false);
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        // Two partial unique indexes: name them, or EF merges indexes on the same columns.
        e.HasIndex(x => new { x.UserId, x.LocalDate }, "ux_digest_scheduled_per_day").IsUnique().HasFilter("kind = 'scheduled'")
            .HasDatabaseName("ux_digest_scheduled_per_day");
        e.HasIndex(x => x.UserId, "ux_digest_manual_inflight").IsUnique()
            .HasFilter("kind = 'manual' AND status IN ('queued','running')").HasDatabaseName("ux_digest_manual_inflight");
        e.HasIndex(x => new { x.UserId, x.CreatedAt }, "ix_digests_user_created").IsDescending(false, true)
            .HasDatabaseName("ix_digests_user_created");
    }
}

internal sealed class DigestItemConfig : IEntityTypeConfiguration<DigestItem>
{
    public void Configure(EntityTypeBuilder<DigestItem> e)
    {
        e.ToTable("digest_items");
        e.HasKey(x => new { x.DigestId, x.ScoreId });
        e.HasOne<Digest>().WithMany().HasForeignKey(x => x.DigestId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<UserJobScore>().WithMany().HasForeignKey(x => x.ScoreId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

// DatabaseDesign §3.6

internal sealed class UserJobConfig : IEntityTypeConfiguration<UserJob>
{
    public void Configure(EntityTypeBuilder<UserJob> e)
    {
        e.ToTable("user_jobs", t =>
        {
            t.HasCheckConstraint("ck_user_jobs_status",
                "status IN ('saved','tailored','applied','interviewing','offer','rejected','withdrawn')");
            t.HasCheckConstraint("ck_user_jobs_fit_score", "fit_score BETWEEN 1 AND 10");
            t.HasCheckConstraint("ck_user_jobs_ats_before", "ats_before BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_user_jobs_ats_after", "ats_after BETWEEN 0 AND 100");
        });
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.IsByo).HasDefaultValue(false);
        e.Property(x => x.UserEnrichment).HasColumnType("jsonb");
        e.Property(x => x.Status).HasDefaultValue(UserJobStatus.Saved);
        e.Property(x => x.StatusHistory).HasColumnType("jsonb").HasDefaultValueSql("'[]'");
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.Version).IsRowVersion();
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.SetNull);
        e.HasIndex(x => new { x.UserId, x.JobId }, "ux_user_jobs_job").IsUnique().HasFilter("job_id IS NOT NULL")
            .HasDatabaseName("ux_user_jobs_job");
        e.HasIndex(x => new { x.UserId, x.Status, x.UpdatedAt }, "ix_user_jobs_status").IsDescending(false, false, true)
            .HasDatabaseName("ix_user_jobs_status");
    }
}

internal sealed class AtsAnalysisConfig : IEntityTypeConfiguration<AtsAnalysis>
{
    public void Configure(EntityTypeBuilder<AtsAnalysis> e)
    {
        e.ToTable("ats_analyses", t =>
        {
            t.HasCheckConstraint("ck_ats_analyses_status", "status IN ('queued','running','ready','failed')");
            t.HasCheckConstraint("ck_ats_analyses_ats_score", "ats_score BETWEEN 0 AND 100");
        });
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.Status).HasDefaultValue(WorkStatus.Queued);
        e.Property(x => x.Components).HasColumnType("jsonb");
        e.Property(x => x.Requirements).HasColumnType("jsonb");
        e.Property(x => x.Gaps).HasColumnType("jsonb");
        e.Property(x => x.Findings).HasColumnType("jsonb");
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        e.HasOne<UserJob>().WithMany().HasForeignKey(x => x.UserJobId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Resume>().WithMany().HasForeignKey(x => x.ResumeId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.UserJobId, x.ResumeId }).IsUnique().HasDatabaseName("ux_ats_analyses_user_job_resume");
    }
}

internal sealed class TailoredDocumentConfig : IEntityTypeConfiguration<TailoredDocument>
{
    public void Configure(EntityTypeBuilder<TailoredDocument> e)
    {
        e.ToTable("tailored_documents", t =>
        {
            t.HasCheckConstraint("ck_tailored_documents_kind", "kind IN ('resume','cover_letter')");
            t.HasCheckConstraint("ck_tailored_documents_status",
                "status IN ('queued','running','proposed','finalizing','ready','failed')");
            t.HasCheckConstraint("ck_tailored_documents_tone", "tone IS NULL OR tone IN ('concise','confident')");
            t.HasCheckConstraint("ck_tailored_documents_ats_after", "ats_after BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_tailored_documents_kind_tone", "(kind = 'resume' AND tone IS NULL) OR (kind = 'cover_letter')");
        });
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.Status).HasDefaultValue(TailoredStatus.Queued);
        e.Property(x => x.Edits).HasColumnType("jsonb");
        e.Property(x => x.Content).HasColumnType("jsonb");
        e.Property(x => x.AtsBreakdown).HasColumnType("jsonb");
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.Version).IsRowVersion();
        e.HasOne<UserJob>().WithMany().HasForeignKey(x => x.UserJobId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Resume>().WithMany().HasForeignKey(x => x.ResumeId).OnDelete(DeleteBehavior.Cascade);
        // Two indexes on (user_job_id, …): name them, or EF merges indexes on the same columns.
        e.HasIndex(x => new { x.UserJobId, x.Kind }, "ux_tailored_inflight").IsUnique()
            .HasFilter("status IN ('queued','running','finalizing')").HasDatabaseName("ux_tailored_inflight");
        e.HasIndex(x => new { x.UserJobId, x.CreatedAt }, "ix_tailored_user_job").IsDescending(false, true)
            .HasDatabaseName("ix_tailored_user_job");
    }
}
