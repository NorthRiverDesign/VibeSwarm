using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class AddJobReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReviewEnabled",
                table: "Projects",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewInferenceProviderId",
                table: "Projects",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "ReviewModelId",
                table: "Projects",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewProviderId",
                table: "Projects",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "ReviewReasoningEffort",
                table: "Projects",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "BuildCheckedTreeHash",
                table: "Jobs",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "ReviewCostUsd",
                table: "Jobs",
                type: "decimal(65,30)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewNotes",
                table: "Jobs",
                type: "longtext",
                maxLength: 8000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ReviewProviderName",
                table: "Jobs",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "ReviewRounds",
                table: "Jobs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReviewStatus",
                table: "Jobs",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewEnabled",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ReviewInferenceProviderId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ReviewModelId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ReviewProviderId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ReviewReasoningEffort",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "BuildCheckedTreeHash",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ReviewCostUsd",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ReviewNotes",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ReviewProviderName",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ReviewRounds",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ReviewStatus",
                table: "Jobs");
        }
    }
}
