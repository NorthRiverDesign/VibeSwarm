using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReasoningEffortSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultReasoningEffort",
                table: "TeamRoles",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultReasoningEffort",
                table: "Providers",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredReasoningEffort",
                table: "ProjectTeamRoles",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanningReasoningEffort",
                table: "Projects",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredReasoningEffort",
                table: "ProjectProviders",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanningReasoningEffortUsed",
                table: "Jobs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReasoningEffort",
                table: "Jobs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultReasoningEffort",
                table: "TeamRoles");

            migrationBuilder.DropColumn(
                name: "DefaultReasoningEffort",
                table: "Providers");

            migrationBuilder.DropColumn(
                name: "PreferredReasoningEffort",
                table: "ProjectTeamRoles");

            migrationBuilder.DropColumn(
                name: "PlanningReasoningEffort",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PreferredReasoningEffort",
                table: "ProjectProviders");

            migrationBuilder.DropColumn(
                name: "PlanningReasoningEffortUsed",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ReasoningEffort",
                table: "Jobs");
        }
    }
}
