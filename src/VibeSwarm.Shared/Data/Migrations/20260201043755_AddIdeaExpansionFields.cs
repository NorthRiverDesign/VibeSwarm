using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIdeaExpansionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpandedAt",
                table: "Ideas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpandedDescription",
                table: "Ideas",
                type: "TEXT",
                maxLength: 10000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpansionError",
                table: "Ideas",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExpansionStatus",
                table: "Ideas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpandedAt",
                table: "Ideas");

            migrationBuilder.DropColumn(
                name: "ExpandedDescription",
                table: "Ideas");

            migrationBuilder.DropColumn(
                name: "ExpansionError",
                table: "Ideas");

            migrationBuilder.DropColumn(
                name: "ExpansionStatus",
                table: "Ideas");
        }
    }
}
