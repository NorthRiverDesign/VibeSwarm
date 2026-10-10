using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
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
                name: "PolishEveryIterations",
                table: "IterationLoops",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "CommitModeOverride",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveIdeaMisses",
                table: "IterationLoops",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IterationsSinceLastPolish",
                table: "IterationLoops",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StatusMessage",
                table: "IterationLoops",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
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
                name: "StatusMessage",
                table: "IterationLoops");

            migrationBuilder.DropColumn(
                name: "PolishEveryIterations",
                table: "IterationLoops");

            migrationBuilder.AddColumn<bool>(
                name: "AutoCommit",
                table: "IterationLoops",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }
    }
}
