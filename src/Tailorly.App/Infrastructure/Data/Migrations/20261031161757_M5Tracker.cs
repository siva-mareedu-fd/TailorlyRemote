using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tailorly.App.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class M5Tracker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "enrichment_error_code",
                table: "user_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "enrichment_status",
                table: "user_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "interview_preps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "queued"),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    content = table.Column<string>(type: "jsonb", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_interview_preps", x => x.id);
                    table.CheckConstraint("ck_interview_preps_status", "status IN ('queued','running','ready','failed')");
                    table.ForeignKey(
                        name: "fk_interview_preps_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_interview_preps_user_jobs_user_job_id",
                        column: x => x.user_job_id,
                        principalTable: "user_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_jobs_enrichment_status",
                table: "user_jobs",
                sql: "enrichment_status IN ('queued','running','ready','failed')");

            migrationBuilder.CreateIndex(
                name: "ix_interview_preps_user_id",
                table: "interview_preps",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_interview_preps_user_job",
                table: "interview_preps",
                column: "user_job_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "interview_preps");

            migrationBuilder.DropCheckConstraint(
                name: "ck_user_jobs_enrichment_status",
                table: "user_jobs");

            migrationBuilder.DropColumn(
                name: "enrichment_error_code",
                table: "user_jobs");

            migrationBuilder.DropColumn(
                name: "enrichment_status",
                table: "user_jobs");
        }
    }
}
