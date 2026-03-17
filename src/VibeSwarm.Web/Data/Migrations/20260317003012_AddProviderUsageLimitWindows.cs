using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderUsageLimitWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LimitWindowsJson",
                table: "ProviderUsageSummaries",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetectedLimitWindowsJson",
                table: "ProviderUsageRecords",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LimitWindowsJson",
                table: "ProviderUsageSummaries");

            migrationBuilder.DropColumn(
                name: "DetectedLimitWindowsJson",
                table: "ProviderUsageRecords");
        }
    }
}
