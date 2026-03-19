using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIterationLoops : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IterationLoopId",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IterationLoops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    MaxIterations = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxTotalCostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                    MaxConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    CooldownSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoCommit = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoPush = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompletedIterations = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalCostUsd = table.Column<decimal>(type: "TEXT", nullable: false),
                    CurrentJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CurrentIdeaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastIterationAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StoppedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextIterationAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStopReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LastUsageCheckResult = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IterationLoops", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IterationLoops_Jobs_CurrentJobId",
                        column: x => x.CurrentJobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_IterationLoops_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IterationLoops_CurrentJobId",
                table: "IterationLoops",
                column: "CurrentJobId");

            migrationBuilder.CreateIndex(
                name: "IX_IterationLoops_ProjectId",
                table: "IterationLoops",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_IterationLoops_Status",
                table: "IterationLoops",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IterationLoops");

            migrationBuilder.DropColumn(
                name: "IterationLoopId",
                table: "Jobs");
        }
    }
}
