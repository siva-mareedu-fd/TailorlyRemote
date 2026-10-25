using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tailorly.App.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class M2Sourcing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    website_domain = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_companies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job_sources",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    requires_attribution = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    attribution_text = table.Column<string>(type: "text", nullable: true),
                    attribution_url = table.Column<string>(type: "text", nullable: true),
                    terms_url = table.Column<string>(type: "text", nullable: true),
                    terms_reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    require_source_apply_url = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    forbid_third_party_resolution = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    purge_on_expiry = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    requires_logo_attribution = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    poll_interval_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 360)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_sources", x => x.key);
                    table.CheckConstraint("ck_job_sources_kind", "kind IN ('feed','board','user')");
                    table.CheckConstraint("ck_job_sources_poll_interval", "poll_interval_minutes > 0");
                });

            migrationBuilder.CreateTable(
                name: "user_preferences",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    search_scope = table.Column<string>(type: "text", nullable: false, defaultValue: "india_plus_global_remote"),
                    target_roles = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    locations = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    work_modes = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{remote}'"),
                    job_types = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{full_time}'"),
                    hiring_models = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{employee,contractor}'"),
                    salary_min = table.Column<long>(type: "bigint", nullable: true),
                    salary_max = table.Column<long>(type: "bigint", nullable: true),
                    salary_currency = table.Column<string>(type: "text", nullable: false, defaultValue: "INR"),
                    experience_level = table.Column<string>(type: "text", nullable: true),
                    min_match_score = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)8),
                    include_unspecified_salary = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    show_unclear_eligibility = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    timezone_overlap = table.Column<string>(type: "text", nullable: true),
                    excluded_companies = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    excluded_keywords = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    schedule_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    schedule_local_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false, defaultValueSql: "'18:00'"),
                    digest_when_empty = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_preferences", x => x.user_id);
                    table.CheckConstraint("ck_user_preferences_min_match_score", "min_match_score BETWEEN 1 AND 10");
                    table.CheckConstraint("ck_user_preferences_salary_currency", "salary_currency IN ('INR','USD')");
                    table.CheckConstraint("ck_user_preferences_salary_max", "salary_max IS NULL OR salary_max >= 0");
                    table.CheckConstraint("ck_user_preferences_salary_min", "salary_min IS NULL OR salary_min >= 0");
                    table.CheckConstraint("ck_user_preferences_salary_range", "salary_max IS NULL OR salary_min IS NULL OR salary_max >= salary_min");
                    table.CheckConstraint("ck_user_preferences_search_scope", "search_scope IN ('india_only','india_plus_global_remote','global_remote_only')");
                    table.ForeignKey(
                        name: "fk_user_preferences_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "company_ats_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    board_token = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "resolved"),
                    origin = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<string>(type: "text", nullable: false, defaultValue: "high"),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    open_jobs_count = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company_ats_links", x => x.id);
                    table.CheckConstraint("ck_company_ats_links_board_token", "board_token ~ '^[a-z0-9_-]{2,60}$'");
                    table.CheckConstraint("ck_company_ats_links_confidence", "confidence IN ('high','medium','low')");
                    table.CheckConstraint("ck_company_ats_links_origin", "origin IN ('seed','user','discovered','guessed','admin')");
                    table.CheckConstraint("ck_company_ats_links_platform", "platform IN ('greenhouse','lever','ashby')");
                    table.CheckConstraint("ck_company_ats_links_status", "status IN ('resolved','not_found','blocked')");
                    table.ForeignKey(
                        name: "fk_company_ats_links_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    dedupe_key = table.Column<string>(type: "text", nullable: false),
                    duplicate_of_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    company_name = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    location_text = table.Column<string>(type: "text", nullable: true),
                    location_class = table.Column<string>(type: "text", nullable: false, defaultValue: "unknown"),
                    work_mode = table.Column<string>(type: "text", nullable: true),
                    employment_type = table.Column<string>(type: "text", nullable: true),
                    hiring_model_hint = table.Column<string>(type: "text", nullable: true),
                    salary_min = table.Column<long>(type: "bigint", nullable: true),
                    salary_max = table.Column<long>(type: "bigint", nullable: true),
                    salary_currency = table.Column<string>(type: "char(3)", nullable: true),
                    salary_period_original = table.Column<string>(type: "text", nullable: true),
                    salary_kind = table.Column<string>(type: "text", nullable: false, defaultValue: "unknown"),
                    description_text = table.Column<string>(type: "text", nullable: true),
                    description_completeness = table.Column<string>(type: "text", nullable: false, defaultValue: "limited"),
                    apply_url = table.Column<string>(type: "text", nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: false),
                    source_meta = table.Column<string>(type: "jsonb", nullable: true),
                    content_hash = table.Column<string>(type: "text", nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_description_completeness", "description_completeness IN ('full','limited')");
                    table.CheckConstraint("ck_jobs_employment_type", "employment_type IN ('full_time','contract','part_time','internship')");
                    table.CheckConstraint("ck_jobs_hiring_model_hint", "hiring_model_hint IN ('employee','contractor','unknown')");
                    table.CheckConstraint("ck_jobs_salary_kind", "salary_kind IN ('stated','estimated','unknown')");
                    table.CheckConstraint("ck_jobs_work_mode", "work_mode IN ('remote','hybrid','onsite')");
                    table.ForeignKey(
                        name: "fk_jobs_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_jobs_job_sources_source_key",
                        column: x => x.source_key,
                        principalTable: "job_sources",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_jobs_jobs_duplicate_of_job_id",
                        column: x => x.duplicate_of_job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "source_health",
                columns: table => new
                {
                    source_key = table.Column<string>(type: "text", nullable: false),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_cursor = table.Column<string>(type: "text", nullable: true),
                    last_failure_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    circuit_open_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    calls_this_month = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    calls_period = table.Column<string>(type: "text", nullable: true),
                    last_run_stats = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_health", x => x.source_key);
                    table.ForeignKey(
                        name: "fk_source_health_job_sources_source_key",
                        column: x => x.source_key,
                        principalTable: "job_sources",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_watched_companies",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_ats_link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_watched_companies", x => new { x.user_id, x.company_ats_link_id });
                    table.ForeignKey(
                        name: "fk_user_watched_companies_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_watched_link",
                        column: x => x.company_ats_link_id,
                        principalTable: "company_ats_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_companies_domain",
                table: "companies",
                column: "website_domain",
                filter: "website_domain IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_companies_normalized",
                table: "companies",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_company_ats_links_company",
                table: "company_ats_links",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_company_ats_links_poll",
                table: "company_ats_links",
                columns: new[] { "status", "next_retry_at" });

            migrationBuilder.CreateIndex(
                name: "ux_company_ats_links_platform_token",
                table: "company_ats_links",
                columns: new[] { "platform", "board_token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_company_id",
                table: "jobs",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_created",
                table: "jobs",
                column: "created_at",
                descending: new bool[0],
                filter: "duplicate_of_job_id IS NULL AND closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_dedupe",
                table: "jobs",
                column: "dedupe_key");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_duplicate_of_job_id",
                table: "jobs",
                column: "duplicate_of_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_expires",
                table: "jobs",
                column: "expires_at",
                filter: "expires_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_jobs_source_external",
                table: "jobs",
                columns: new[] { "source_key", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_watched_companies_company_ats_link_id",
                table: "user_watched_companies",
                column: "company_ats_link_id");

            // DatabaseDesign §3.4: title + company text search for the in-app list (SDD §12.6). Not mapped in EF.
            migrationBuilder.Sql("""
                ALTER TABLE jobs ADD COLUMN search_tsv tsvector GENERATED ALWAYS AS
                  (to_tsvector('simple', coalesce(title,'') || ' ' || coalesce(company_name,''))) STORED;
                CREATE INDEX ix_jobs_search ON jobs USING gin (search_tsv);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "source_health");

            migrationBuilder.DropTable(
                name: "user_preferences");

            migrationBuilder.DropTable(
                name: "user_watched_companies");

            migrationBuilder.DropTable(
                name: "job_sources");

            migrationBuilder.DropTable(
                name: "company_ats_links");

            migrationBuilder.DropTable(
                name: "companies");
        }
    }
}
