using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobChangeSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobChangeSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FollowUpIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    GitCommitHash = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    GitCommitBefore = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ChangedFilesCount = table.Column<int>(type: "INTEGER", nullable: true),
                    SessionSummary = table.Column<string>(type: "TEXT", nullable: true),
                    PullRequestNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    PullRequestUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    MergedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BuildVerified = table.Column<bool>(type: "INTEGER", nullable: true),
                    ModelUsed = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobChangeSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobChangeSets_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobChangeSets_JobId",
                table: "JobChangeSets",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobChangeSets_JobId_FollowUpIndex",
                table: "JobChangeSets",
                columns: new[] { "JobId", "FollowUpIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobChangeSets");
        }
    }
}
