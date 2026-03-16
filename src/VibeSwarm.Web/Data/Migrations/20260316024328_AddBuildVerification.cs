using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BuildCommand",
                table: "Projects",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BuildVerificationEnabled",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TestCommand",
                table: "Projects",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuildOutput",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BuildVerified",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuildCommand",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "BuildVerificationEnabled",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "TestCommand",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "BuildOutput",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "BuildVerified",
                table: "Jobs");
        }
    }
}
