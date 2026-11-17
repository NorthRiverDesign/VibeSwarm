using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class PolishAutoPilotLoops : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoCommit",
                table: "IterationLoops");

            migrationBuilder.AddColumn<int>(
                name: "CommitModeOverride",
                table: "Jobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveIdeaMisses",
                table: "IterationLoops",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IterationsSinceLastPolish",
                table: "IterationLoops",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PolishEveryIterations",
                table: "IterationLoops",
                type: "int",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<string>(
                name: "StatusMessage",
                table: "IterationLoops",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommitModeOverride",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ConsecutiveIdeaMisses",
                table: "IterationLoops");

            migrationBuilder.DropColumn(
                name: "IterationsSinceLastPolish",
                table: "IterationLoops");

            migrationBuilder.DropColumn(
                name: "PolishEveryIterations",
                table: "IterationLoops");

            migrationBuilder.DropColumn(
                name: "StatusMessage",
                table: "IterationLoops");

            migrationBuilder.AddColumn<bool>(
                name: "AutoCommit",
                table: "IterationLoops",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);
        }
    }
}
