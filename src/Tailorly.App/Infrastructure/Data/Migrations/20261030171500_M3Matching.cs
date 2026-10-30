using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tailorly.App.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class M3Matching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "notification_email_verified_at",
                table: "user_profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "digests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "queued"),
                    attempt = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    jobs_considered = table.Column<int>(type: "integer", nullable: true),
                    jobs_scored = table.Column<int>(type: "integer", nullable: true),
                    degraded = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    emailed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_digests", x => x.id);
                    table.CheckConstraint("ck_digests_kind", "kind IN ('scheduled','manual')");
                    table.CheckConstraint("ck_digests_status", "status IN ('queued','running','ready','failed')");
                    table.ForeignKey(
                        name: "fk_digests_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "job_enrichment",
                columns: table => new
                {
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    eligibility = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    schema_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_enrichment", x => x.job_id);
                    table.CheckConstraint("ck_job_enrichment_eligibility", "eligibility IN ('worldwide','india_eligible','restricted','unclear')");
                    table.ForeignKey(
                        name: "fk_job_enrichment_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_job_scores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resume_version = table.Column<int>(type: "integer", nullable: false),
                    fit_score = table.Column<short>(type: "smallint", nullable: false),
                    reasons = table.Column<string>(type: "jsonb", nullable: false),
                    matched_skills = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'"),
                    missing_skills = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'"),
                    confidence = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_job_scores", x => x.id);
                    table.CheckConstraint("ck_user_job_scores_confidence", "confidence IN ('high','medium','low')");
                    table.CheckConstraint("ck_user_job_scores_fit_score", "fit_score BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "fk_user_job_scores_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_job_scores_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "digest_items",
                columns: table => new
                {
                    digest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    score_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rank = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_digest_items", x => new { x.digest_id, x.score_id });
                    table.ForeignKey(
                        name: "fk_digest_items_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_digest_items_digests_digest_id",
                        column: x => x.digest_id,
                        principalTable: "digests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_digest_items_user_job_scores_score_id",
                        column: x => x.score_id,
                        principalTable: "user_job_scores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_digest_items_score_id",
                table: "digest_items",
                column: "score_id");

            migrationBuilder.CreateIndex(
                name: "ix_digest_items_user_id",
                table: "digest_items",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_digests_user_created",
                table: "digests",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_digest_manual_inflight",
                table: "digests",
                column: "user_id",
                unique: true,
                filter: "kind = 'manual' AND status IN ('queued','running')");

            migrationBuilder.CreateIndex(
                name: "ux_digest_scheduled_per_day",
                table: "digests",
                columns: new[] { "user_id", "local_date" },
                unique: true,
                filter: "kind = 'scheduled'");

            migrationBuilder.CreateIndex(
                name: "ix_job_enrichment_eligibility",
                table: "job_enrichment",
                column: "eligibility");

            migrationBuilder.CreateIndex(
                name: "ix_scores_user_created",
                table: "user_job_scores",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_user_job_scores_job_id",
                table: "user_job_scores",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ux_user_job_scores_user_job_version",
                table: "user_job_scores",
                columns: new[] { "user_id", "job_id", "resume_version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "digest_items");

            migrationBuilder.DropTable(
                name: "job_enrichment");

            migrationBuilder.DropTable(
                name: "digests");

            migrationBuilder.DropTable(
                name: "user_job_scores");

            migrationBuilder.DropColumn(
                name: "notification_email_verified_at",
                table: "user_profiles");
        }
    }
}
