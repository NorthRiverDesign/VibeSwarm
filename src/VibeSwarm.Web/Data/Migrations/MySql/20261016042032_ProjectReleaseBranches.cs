using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
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
                type: "varchar(250)",
                maxLength: 250,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ReleaseBranch",
                table: "Projects",
                type: "varchar(250)",
                maxLength: 250,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "MergeFromBranch",
                table: "Jobs",
                type: "varchar(250)",
                maxLength: 250,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
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
