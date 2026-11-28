using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class RemoveCycleModes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CycleMode",
                table: "JobTemplates");

            migrationBuilder.DropColumn(
                name: "CycleReviewPrompt",
                table: "JobTemplates");

            migrationBuilder.DropColumn(
                name: "CycleSessionMode",
                table: "JobTemplates");

            migrationBuilder.DropColumn(
                name: "MaxCycles",
                table: "JobTemplates");

            migrationBuilder.DropColumn(
                name: "CurrentCycle",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "CycleMode",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "CycleReviewPrompt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "CycleSessionMode",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "MaxCycles",
                table: "Jobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CycleMode",
                table: "JobTemplates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CycleReviewPrompt",
                table: "JobTemplates",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CycleSessionMode",
                table: "JobTemplates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxCycles",
                table: "JobTemplates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CurrentCycle",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CycleMode",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CycleReviewPrompt",
                table: "Jobs",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CycleSessionMode",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxCycles",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
