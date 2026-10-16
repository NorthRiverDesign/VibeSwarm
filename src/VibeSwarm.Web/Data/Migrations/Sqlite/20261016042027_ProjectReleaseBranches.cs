using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ProjectReleaseBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DevelopmentBranch",
                table: "Projects",
                type: "TEXT",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseBranch",
                table: "Projects",
                type: "TEXT",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeFromBranch",
                table: "Jobs",
                type: "TEXT",
                maxLength: 250,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DevelopmentBranch",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ReleaseBranch",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "MergeFromBranch",
                table: "Jobs");
        }
    }
}
