using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsScheduled",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "JobScheduleId",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledForUtc",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JobSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Frequency = table.Column<string>(type: "TEXT", nullable: false),
                    HourUtc = table.Column<int>(type: "INTEGER", nullable: false),
                    MinuteUtc = table.Column<int>(type: "INTEGER", nullable: false),
                    WeeklyDay = table.Column<string>(type: "TEXT", nullable: false),
                    DayOfMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobSchedules_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobSchedules_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_JobScheduleId_ScheduledForUtc",
                table: "Jobs",
                columns: new[] { "JobScheduleId", "ScheduledForUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobSchedules_IsEnabled_NextRunAtUtc",
                table: "JobSchedules",
                columns: new[] { "IsEnabled", "NextRunAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobSchedules_ProjectId_IsEnabled",
                table: "JobSchedules",
                columns: new[] { "ProjectId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_JobSchedules_ProviderId",
                table: "JobSchedules",
                column: "ProviderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_JobSchedules_JobScheduleId",
                table: "Jobs",
                column: "JobScheduleId",
                principalTable: "JobSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_JobSchedules_JobScheduleId",
                table: "Jobs");

            migrationBuilder.DropTable(
                name: "JobSchedules");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_JobScheduleId_ScheduledForUtc",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "IsScheduled",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "JobScheduleId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ScheduledForUtc",
                table: "Jobs");
        }
    }
}
