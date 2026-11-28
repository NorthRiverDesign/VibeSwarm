using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class AddTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlanningModelId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PlanningProfileId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PlanningProviderId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PlanningReasoningEffort",
                table: "Projects");

            migrationBuilder.RenameColumn(
                name: "PlanningEnabled",
                table: "Projects",
                newName: "TeamEnabled");

            migrationBuilder.AddColumn<int>(
                name: "TeamOverrides",
                table: "Projects",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PlanSkippedReason",
                table: "Jobs",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "TeamRun",
                table: "Jobs",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TeamLeadProfileId",
                table: "AppSettings",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "TeamReviewProfileId",
                table: "AppSettings",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TeamOverrides",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PlanSkippedReason",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "TeamRun",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "TeamLeadProfileId",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "TeamReviewProfileId",
                table: "AppSettings");

            migrationBuilder.RenameColumn(
                name: "TeamEnabled",
                table: "Projects",
                newName: "PlanningEnabled");

            migrationBuilder.AddColumn<string>(
                name: "PlanningModelId",
                table: "Projects",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "PlanningProfileId",
                table: "Projects",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "PlanningProviderId",
                table: "Projects",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "PlanningReasoningEffort",
                table: "Projects",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }
    }
}
