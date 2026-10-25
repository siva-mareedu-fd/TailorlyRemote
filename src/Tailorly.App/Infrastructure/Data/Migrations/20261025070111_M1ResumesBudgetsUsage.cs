using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Tailorly.App.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class M1ResumesBudgetsUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_usage",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    task = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    bucket = table.Column<string>(type: "text", nullable: false),
                    period_key = table.Column<string>(type: "text", nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    output_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    cache_create_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    cache_read_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    counted_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    est_cost_usd = table.Column<decimal>(type: "numeric(10,5)", precision: 10, scale: 5, nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_usage", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_usage_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "resumes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    original_filename = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    parse_status = table.Column<string>(type: "text", nullable: false, defaultValue: "queued"),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    format_findings = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resumes", x => x.id);
                    table.CheckConstraint("ck_resumes_parse_status", "parse_status IN ('queued','running','ready','failed')");
                    table.ForeignKey(
                        name: "fk_resumes_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_budget_counters",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_key = table.Column<string>(type: "text", nullable: false),
                    bucket = table.Column<string>(type: "text", nullable: false),
                    limit_tokens = table.Column<long>(type: "bigint", nullable: false),
                    used_tokens = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_budget_counters", x => new { x.user_id, x.period_key, x.bucket });
                    table.CheckConstraint("ck_user_budget_counters_bucket", "bucket IN ('discovery','documents')");
                    table.CheckConstraint("ck_user_budget_counters_limit", "limit_tokens >= 0");
                    table.CheckConstraint("ck_user_budget_counters_used", "used_tokens >= 0");
                    table.ForeignKey(
                        name: "fk_user_budget_counters_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_confirmed_skills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    skill = table.Column<string>(type: "text", nullable: false),
                    skill_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_confirmed_skills", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_confirmed_skills_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "resume_profiles",
                columns: table => new
                {
                    resume_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parsed = table.Column<string>(type: "jsonb", nullable: false),
                    user_edited = table.Column<string>(type: "jsonb", nullable: true),
                    schema_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resume_profiles", x => x.resume_id);
                    table.ForeignKey(
                        name: "fk_resume_profiles_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_resume_profiles_resumes_resume_id",
                        column: x => x.resume_id,
                        principalTable: "resumes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_platform_day",
                table: "ai_usage",
                column: "created_at",
                filter: "user_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_user_period",
                table: "ai_usage",
                columns: new[] { "user_id", "period_key", "bucket" });

            migrationBuilder.CreateIndex(
                name: "ix_resume_profiles_user_id",
                table: "resume_profiles",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_resumes_one_active",
                table: "resumes",
                column: "user_id",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ux_resumes_parse_inflight",
                table: "resumes",
                column: "user_id",
                unique: true,
                filter: "parse_status IN ('queued','running')");

            migrationBuilder.CreateIndex(
                name: "ux_resumes_user_version",
                table: "resumes",
                columns: new[] { "user_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_user_confirmed_skills_key",
                table: "user_confirmed_skills",
                columns: new[] { "user_id", "skill_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_usage");

            migrationBuilder.DropTable(
                name: "resume_profiles");

            migrationBuilder.DropTable(
                name: "user_budget_counters");

            migrationBuilder.DropTable(
                name: "user_confirmed_skills");

            migrationBuilder.DropTable(
                name: "resumes");
        }
    }
}
