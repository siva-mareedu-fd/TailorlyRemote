using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tailorly.App.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class M4Tailoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_byo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    company_name = table.Column<string>(type: "text", nullable: false),
                    apply_url = table.Column<string>(type: "text", nullable: true),
                    source_key = table.Column<string>(type: "text", nullable: true),
                    source_name = table.Column<string>(type: "text", nullable: true),
                    attribution_url = table.Column<string>(type: "text", nullable: true),
                    user_description = table.Column<string>(type: "text", nullable: true),
                    user_enrichment = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "saved"),
                    notes = table.Column<string>(type: "text", nullable: true),
                    fit_score = table.Column<short>(type: "smallint", nullable: true),
                    ats_before = table.Column<short>(type: "smallint", nullable: true),
                    ats_after = table.Column<short>(type: "smallint", nullable: true),
                    status_history = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_jobs", x => x.id);
                    table.CheckConstraint("ck_user_jobs_ats_after", "ats_after BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_user_jobs_ats_before", "ats_before BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_user_jobs_fit_score", "fit_score BETWEEN 1 AND 10");
                    table.CheckConstraint("ck_user_jobs_status", "status IN ('saved','tailored','applied','interviewing','offer','rejected','withdrawn')");
                    table.ForeignKey(
                        name: "fk_user_jobs_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_jobs_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ats_analyses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resume_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "queued"),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    ats_score = table.Column<short>(type: "smallint", nullable: true),
                    components = table.Column<string>(type: "jsonb", nullable: true),
                    requirements = table.Column<string>(type: "jsonb", nullable: true),
                    gaps = table.Column<string>(type: "jsonb", nullable: true),
                    findings = table.Column<string>(type: "jsonb", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ats_analyses", x => x.id);
                    table.CheckConstraint("ck_ats_analyses_ats_score", "ats_score BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_ats_analyses_status", "status IN ('queued','running','ready','failed')");
                    table.ForeignKey(
                        name: "fk_ats_analyses_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ats_analyses_resumes_resume_id",
                        column: x => x.resume_id,
                        principalTable: "resumes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ats_analyses_user_jobs_user_job_id",
                        column: x => x.user_job_id,
                        principalTable: "user_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tailored_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resume_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "queued"),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    source_resume_version = table.Column<int>(type: "integer", nullable: false),
                    edits = table.Column<string>(type: "jsonb", nullable: true),
                    content = table.Column<string>(type: "jsonb", nullable: true),
                    tone = table.Column<string>(type: "text", nullable: true),
                    ats_after = table.Column<short>(type: "smallint", nullable: true),
                    ats_breakdown = table.Column<string>(type: "jsonb", nullable: true),
                    storage_key = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    finalized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tailored_documents", x => x.id);
                    table.CheckConstraint("ck_tailored_documents_ats_after", "ats_after BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_tailored_documents_kind", "kind IN ('resume','cover_letter')");
                    table.CheckConstraint("ck_tailored_documents_kind_tone", "(kind = 'resume' AND tone IS NULL) OR (kind = 'cover_letter')");
                    table.CheckConstraint("ck_tailored_documents_status", "status IN ('queued','running','proposed','finalizing','ready','failed')");
                    table.CheckConstraint("ck_tailored_documents_tone", "tone IS NULL OR tone IN ('concise','confident')");
                    table.ForeignKey(
                        name: "fk_tailored_documents_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_tailored_documents_resumes_resume_id",
                        column: x => x.resume_id,
                        principalTable: "resumes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_tailored_documents_user_jobs_user_job_id",
                        column: x => x.user_job_id,
                        principalTable: "user_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ats_analyses_resume_id",
                table: "ats_analyses",
                column: "resume_id");

            migrationBuilder.CreateIndex(
                name: "ix_ats_analyses_user_id",
                table: "ats_analyses",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_ats_analyses_user_job_resume",
                table: "ats_analyses",
                columns: new[] { "user_job_id", "resume_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tailored_documents_resume_id",
                table: "tailored_documents",
                column: "resume_id");

            migrationBuilder.CreateIndex(
                name: "ix_tailored_documents_user_id",
                table: "tailored_documents",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_tailored_user_job",
                table: "tailored_documents",
                columns: new[] { "user_job_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_tailored_inflight",
                table: "tailored_documents",
                columns: new[] { "user_job_id", "kind" },
                unique: true,
                filter: "status IN ('queued','running','finalizing')");

            migrationBuilder.CreateIndex(
                name: "ix_user_jobs_job_id",
                table: "user_jobs",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_jobs_status",
                table: "user_jobs",
                columns: new[] { "user_id", "status", "updated_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ux_user_jobs_job",
                table: "user_jobs",
                columns: new[] { "user_id", "job_id" },
                unique: true,
                filter: "job_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ats_analyses");

            migrationBuilder.DropTable(
                name: "tailored_documents");

            migrationBuilder.DropTable(
                name: "user_jobs");
        }
    }
}
