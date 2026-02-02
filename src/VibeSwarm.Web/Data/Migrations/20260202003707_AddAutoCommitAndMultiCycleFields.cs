using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoCommitAndMultiCycleFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AutoCommitMode",
                table: "Projects",
                type: "TEXT",
                nullable: false,
                defaultValue: "Off");

            migrationBuilder.AddColumn<int>(
                name: "CurrentCycle",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "CycleMode",
                table: "Jobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "SingleCycle");

            migrationBuilder.AddColumn<string>(
                name: "CycleReviewPrompt",
                table: "Jobs",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CycleSessionMode",
                table: "Jobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "ContinueSession");

            migrationBuilder.AddColumn<int>(
                name: "MaxCycles",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoCommitMode",
                table: "Projects");

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
    }
}
