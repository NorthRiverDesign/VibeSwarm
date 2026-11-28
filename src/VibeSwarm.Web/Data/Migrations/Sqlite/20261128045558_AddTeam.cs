using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
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
                newName: "TeamOverrides");

            migrationBuilder.AddColumn<bool>(
                name: "TeamEnabled",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PlanSkippedReason",
                table: "Jobs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TeamRun",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TeamLeadProfileId",
                table: "AppSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TeamReviewProfileId",
                table: "AppSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TeamEnabled",
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
                name: "TeamOverrides",
                table: "Projects",
                newName: "PlanningEnabled");

            migrationBuilder.AddColumn<string>(
                name: "PlanningModelId",
                table: "Projects",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlanningProfileId",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlanningProviderId",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanningReasoningEffort",
                table: "Projects",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }
    }
}
