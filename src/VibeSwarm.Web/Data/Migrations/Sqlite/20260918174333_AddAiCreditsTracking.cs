using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
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
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AiCreditsConsumed",
                table: "ProviderUsageRecords",
                type: "TEXT",
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
