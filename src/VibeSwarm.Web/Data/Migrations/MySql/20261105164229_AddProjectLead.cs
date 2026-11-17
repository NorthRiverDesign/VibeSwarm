using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
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
                type: "longtext",
                maxLength: 4000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "LeadModelId",
                table: "Projects",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "LeadProviderId",
                table: "Projects",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "LeadReasoningEffort",
                table: "Projects",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovalNudgeSentAt",
                table: "IterationLoops",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdeaSource",
                table: "IterationLoops",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "DismissedAt",
                table: "Ideas",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeadReviewId",
                table: "Ideas",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<bool>(
                name: "NeedsApproval",
                table: "Ideas",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "Ideas",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LeadReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ProjectId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Trigger = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProviderName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModelId = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Content = table.Column<string>(type: "longtext", maxLength: 20000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IdeasCreated = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "longtext", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InputTokens = table.Column<int>(type: "int", nullable: true),
                    OutputTokens = table.Column<int>(type: "int", nullable: true),
                    CostUsd = table.Column<decimal>(type: "decimal(65,30)", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    PreviousReviewId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

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
