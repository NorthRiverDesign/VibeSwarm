using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptStructuringAndRepoMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PromptContext",
                table: "Projects",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepoMap",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RepoMapGeneratedAt",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnablePromptStructuring",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "InjectEfficiencyRules",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "InjectRepoMap",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PromptContext",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RepoMap",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RepoMapGeneratedAt",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "EnablePromptStructuring",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "InjectEfficiencyRules",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "InjectRepoMap",
                table: "AppSettings");
        }
    }
}
