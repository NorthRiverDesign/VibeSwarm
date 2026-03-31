using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCriticalErrorLogging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CriticalErrorLogMaxEntries",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 200);

            migrationBuilder.AddColumn<int>(
                name: "CriticalErrorLogRetentionDays",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.CreateTable(
                name: "CriticalErrorLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Details = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RefreshAction = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TriggeredRefresh = table.Column<bool>(type: "INTEGER", nullable: false),
                    AdditionalDataJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CriticalErrorLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CriticalErrorLogs_CreatedAt",
                table: "CriticalErrorLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CriticalErrorLogs_Source_CreatedAt",
                table: "CriticalErrorLogs",
                columns: new[] { "Source", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CriticalErrorLogs");

            migrationBuilder.DropColumn(
                name: "CriticalErrorLogMaxEntries",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "CriticalErrorLogRetentionDays",
                table: "AppSettings");
        }
    }
}
