using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
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
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    JobId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Instructions = table.Column<string>(type: "longtext", maxLength: 8000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DoneWhen = table.Column<string>(type: "longtext", maxLength: 4000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Summary = table.Column<string>(type: "longtext", maxLength: 4000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReviewStatus = table.Column<int>(type: "int", nullable: false),
                    ReviewRounds = table.Column<int>(type: "int", nullable: false),
                    ReviewNotes = table.Column<string>(type: "longtext", maxLength: 8000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SessionId = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SnapshotCommit = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InputTokens = table.Column<int>(type: "int", nullable: true),
                    OutputTokens = table.Column<int>(type: "int", nullable: true),
                    CostUsd = table.Column<decimal>(type: "decimal(65,30)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

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
