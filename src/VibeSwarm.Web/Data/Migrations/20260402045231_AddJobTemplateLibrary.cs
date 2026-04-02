using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobTemplateLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "JobTemplateId",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JobTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    GoalPrompt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    GitChangeDeliveryMode = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetBranch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    CycleMode = table.Column<int>(type: "INTEGER", nullable: false),
                    CycleSessionMode = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxCycles = table.Column<int>(type: "INTEGER", nullable: false),
                    CycleReviewPrompt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UseCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobTemplates_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_JobTemplateId",
                table: "Jobs",
                column: "JobTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTemplates_CreatedAt",
                table: "JobTemplates",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_JobTemplates_Name",
                table: "JobTemplates",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobTemplates_ProviderId",
                table: "JobTemplates",
                column: "ProviderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_JobTemplates_JobTemplateId",
                table: "Jobs",
                column: "JobTemplateId",
                principalTable: "JobTemplates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_JobTemplates_JobTemplateId",
                table: "Jobs");

            migrationBuilder.DropTable(
                name: "JobTemplates");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_JobTemplateId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "JobTemplateId",
                table: "Jobs");
        }
    }
}
