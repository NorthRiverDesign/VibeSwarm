using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class AddAiCreditsTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TotalAiCreditsConsumed",
                table: "ProviderUsageSummaries",
                type: "decimal(65,30)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AiCreditsConsumed",
                table: "ProviderUsageRecords",
                type: "decimal(65,30)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TotalAiCreditsConsumed",
                table: "ProviderUsageSummaries");

            migrationBuilder.DropColumn(
                name: "AiCreditsConsumed",
                table: "ProviderUsageRecords");
        }
    }
}
