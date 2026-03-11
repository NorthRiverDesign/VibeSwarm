using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectProviderPriorityAndFallbackTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveExecutionIndex",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionPlan",
                table: "Jobs",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSwitchAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSwitchReason",
                table: "Jobs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JobProviderAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AttemptOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    WasSuccessful = table.Column<bool>(type: "INTEGER", nullable: false),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobProviderAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobProviderAttempts_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    PreferredModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectProviders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectProviders_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectProviders_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobProviderAttempts_JobId_AttemptedAt",
                table: "JobProviderAttempts",
                columns: new[] { "JobId", "AttemptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_JobProviderAttempts_JobId_AttemptOrder",
                table: "JobProviderAttempts",
                columns: new[] { "JobId", "AttemptOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectProviders_ProjectId_Priority",
                table: "ProjectProviders",
                columns: new[] { "ProjectId", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectProviders_ProjectId_ProviderId",
                table: "ProjectProviders",
                columns: new[] { "ProjectId", "ProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectProviders_ProviderId",
                table: "ProjectProviders",
                column: "ProviderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobProviderAttempts");

            migrationBuilder.DropTable(
                name: "ProjectProviders");

            migrationBuilder.DropColumn(
                name: "ActiveExecutionIndex",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ExecutionPlan",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "LastSwitchAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "LastSwitchReason",
                table: "Jobs");
        }
    }
}
