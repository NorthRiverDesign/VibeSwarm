using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddProjectLead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LeadDirection",
                table: "Projects",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeadModelId",
                table: "Projects",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeadProviderId",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeadReasoningEffort",
                table: "Projects",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovalNudgeSentAt",
                table: "IterationLoops",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdeaSource",
                table: "IterationLoops",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "DismissedAt",
                table: "Ideas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeadReviewId",
                table: "Ideas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsApproval",
                table: "Ideas",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "Ideas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LeadReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Content = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: true),
                    IdeasCreated = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    PreviousReviewId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadReviews_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Ideas_ProjectId_NeedsApproval",
                table: "Ideas",
                columns: new[] { "ProjectId", "NeedsApproval" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadReviews_ProjectId_CreatedAt",
                table: "LeadReviews",
                columns: new[] { "ProjectId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeadReviews");

            migrationBuilder.DropIndex(
                name: "IX_Ideas_ProjectId_NeedsApproval",
                table: "Ideas");

            migrationBuilder.DropColumn(
                name: "LeadDirection",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "LeadModelId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "LeadProviderId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "LeadReasoningEffort",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ApprovalNudgeSentAt",
                table: "IterationLoops");

            migrationBuilder.DropColumn(
                name: "IdeaSource",
                table: "IterationLoops");

            migrationBuilder.DropColumn(
                name: "DismissedAt",
                table: "Ideas");

            migrationBuilder.DropColumn(
                name: "LeadReviewId",
                table: "Ideas");

            migrationBuilder.DropColumn(
                name: "NeedsApproval",
                table: "Ideas");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Ideas");
        }
    }
}
