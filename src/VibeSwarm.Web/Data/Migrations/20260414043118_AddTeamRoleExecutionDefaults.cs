using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamRoleExecutionDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultCycleMode",
                table: "TeamRoles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DefaultCycleReviewPrompt",
                table: "TeamRoles",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultCycleSessionMode",
                table: "TeamRoles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DefaultMaxCycles",
                table: "TeamRoles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultCycleMode",
                table: "TeamRoles");

            migrationBuilder.DropColumn(
                name: "DefaultCycleReviewPrompt",
                table: "TeamRoles");

            migrationBuilder.DropColumn(
                name: "DefaultCycleSessionMode",
                table: "TeamRoles");

            migrationBuilder.DropColumn(
                name: "DefaultMaxCycles",
                table: "TeamRoles");
        }
    }
}
