using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillInstallMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedTools",
                table: "Skills",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasScripts",
                table: "Skills",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "InstalledAt",
                table: "Skills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceRef",
                table: "Skills",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "Skills",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<string>(
                name: "SourceUri",
                table: "Skills",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoragePath",
                table: "Skills",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedTools",
                table: "Skills");

            migrationBuilder.DropColumn(
                name: "HasScripts",
                table: "Skills");

            migrationBuilder.DropColumn(
                name: "InstalledAt",
                table: "Skills");

            migrationBuilder.DropColumn(
                name: "SourceRef",
                table: "Skills");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "Skills");

            migrationBuilder.DropColumn(
                name: "SourceUri",
                table: "Skills");

            migrationBuilder.DropColumn(
                name: "StoragePath",
                table: "Skills");
        }
    }
}
