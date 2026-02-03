using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderUsageTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfiguredLimitType",
                table: "Providers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ConfiguredUsageLimit",
                table: "Providers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProviderUsageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CostUsd = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    PremiumRequestsConsumed = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelUsed = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DetectedLimitType = table.Column<string>(type: "TEXT", nullable: true),
                    DetectedCurrentUsage = table.Column<int>(type: "INTEGER", nullable: true),
                    DetectedMaxUsage = table.Column<int>(type: "INTEGER", nullable: true),
                    DetectedResetTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DetectedLimitReached = table.Column<bool>(type: "INTEGER", nullable: false),
                    RawLimitMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderUsageRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderUsageRecords_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProviderUsageRecords_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProviderUsageSummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TotalInputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalOutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalCostUsd = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    TotalJobsCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalPremiumRequestsConsumed = table.Column<int>(type: "INTEGER", nullable: false),
                    LimitType = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentUsage = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxUsage = table.Column<int>(type: "INTEGER", nullable: true),
                    LimitResetTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsLimitReached = table.Column<bool>(type: "INTEGER", nullable: false),
                    LimitMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ConfiguredMaxUsage = table.Column<int>(type: "INTEGER", nullable: true),
                    CliVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    VersionCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PeriodStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderUsageSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderUsageSummaries_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageRecords_JobId",
                table: "ProviderUsageRecords",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageRecords_ProviderId_RecordedAt",
                table: "ProviderUsageRecords",
                columns: new[] { "ProviderId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageSummaries_ProviderId",
                table: "ProviderUsageSummaries",
                column: "ProviderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderUsageRecords");

            migrationBuilder.DropTable(
                name: "ProviderUsageSummaries");

            migrationBuilder.DropColumn(
                name: "ConfiguredLimitType",
                table: "Providers");

            migrationBuilder.DropColumn(
                name: "ConfiguredUsageLimit",
                table: "Providers");
        }
    }
}
