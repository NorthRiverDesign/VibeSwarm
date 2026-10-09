using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddSessionLimitPauseThreshold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LimitsRefreshedAt",
                table: "ProviderUsageSummaries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SessionLimitPauseThresholdPercent",
                table: "Providers",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LimitsRefreshedAt",
                table: "ProviderUsageSummaries");

            migrationBuilder.DropColumn(
                name: "SessionLimitPauseThresholdPercent",
                table: "Providers");
        }
    }
}
