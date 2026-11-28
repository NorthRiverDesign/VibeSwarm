using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class RemoveIdeaExpansion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpandedAt",
                table: "Ideas");

            migrationBuilder.DropColumn(
                name: "ExpansionError",
                table: "Ideas");

            migrationBuilder.DropColumn(
                name: "ExpansionStatus",
                table: "Ideas");

            migrationBuilder.DropColumn(
                name: "IdeaExpansionPromptTemplate",
                table: "AppSettings");

            migrationBuilder.RenameColumn(
                name: "ExpandedDescription",
                table: "Ideas",
                newName: "Specification");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Specification",
                table: "Ideas",
                newName: "ExpandedDescription");

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpandedAt",
                table: "Ideas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpansionError",
                table: "Ideas",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpansionStatus",
                table: "Ideas",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IdeaExpansionPromptTemplate",
                table: "AppSettings",
                type: "TEXT",
                maxLength: 12000,
                nullable: true);
        }
    }
}
