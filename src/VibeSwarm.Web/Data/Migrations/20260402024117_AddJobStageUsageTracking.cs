using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobStageUsageTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ExecutionCostUsd",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExecutionInputTokens",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExecutionOutputTokens",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PlanningCostUsd",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlanningInputTokens",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlanningOutputTokens",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_PlanningProviderId",
                table: "Jobs",
                column: "PlanningProviderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Providers_PlanningProviderId",
                table: "Jobs",
                column: "PlanningProviderId",
                principalTable: "Providers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_Providers_PlanningProviderId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_PlanningProviderId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ExecutionCostUsd",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ExecutionInputTokens",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ExecutionOutputTokens",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PlanningCostUsd",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PlanningInputTokens",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PlanningOutputTokens",
                table: "Jobs");
        }
    }
}
