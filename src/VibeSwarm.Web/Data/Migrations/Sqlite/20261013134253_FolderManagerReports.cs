using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class FolderManagerReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FolderManagerReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PreviousReportId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Content = table.Column<string>(type: "TEXT", maxLength: 100000, nullable: false),
                    ProjectNames = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProviderType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    CostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderManagerReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FolderManagerReports_ProjectFolders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "ProjectFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FolderManagerReports_FolderId_CreatedAt",
                table: "FolderManagerReports",
                columns: new[] { "FolderId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FolderManagerReports");
        }
    }
}
