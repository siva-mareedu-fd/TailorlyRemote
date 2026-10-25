using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tailorly.App.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class M1ResumeVersionCounter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "resume_versions_issued",
                table: "user_profiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing accounts continue from their highest current version.
            migrationBuilder.Sql("""
                UPDATE user_profiles p SET resume_versions_issued = v.max_version
                FROM (SELECT user_id, max(version) AS max_version FROM resumes GROUP BY user_id) v
                WHERE v.user_id = p.user_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "resume_versions_issued",
                table: "user_profiles");
        }
    }
}
