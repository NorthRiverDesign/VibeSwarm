using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubRepositoryToProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GitHubRepository",
                table: "Projects",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitHubRepository",
                table: "Projects");
        }
    }
}
