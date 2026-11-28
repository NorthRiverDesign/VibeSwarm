using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class AddJobAndTemplateProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProfileId",
                table: "JobTemplates",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "ProfileId",
                table: "Jobs",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "ProfileName",
                table: "Jobs",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "JobTemplates");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ProfileName",
                table: "Jobs");
        }
    }
}
