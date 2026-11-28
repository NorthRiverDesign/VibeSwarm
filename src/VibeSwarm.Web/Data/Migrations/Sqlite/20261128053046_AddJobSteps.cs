using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddJobSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Instructions = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    DoneWhen = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ReviewStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewRounds = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewNotes = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SnapshotCommit = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobSteps_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobSteps_JobId_Order",
                table: "JobSteps",
                columns: new[] { "JobId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobSteps");
        }
    }
}
