using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobInteractionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InteractionChoices",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InteractionRequestedAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InteractionType",
                table: "Jobs",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingInteractionPrompt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InteractionChoices",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "InteractionRequestedAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "InteractionType",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PendingInteractionPrompt",
                table: "Jobs");
        }
    }
}
