using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamRoleDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultModelId",
                table: "TeamRoles",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultProviderId",
                table: "TeamRoles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamRoles_DefaultProviderId",
                table: "TeamRoles",
                column: "DefaultProviderId");

            migrationBuilder.AddForeignKey(
                name: "FK_TeamRoles_Providers_DefaultProviderId",
                table: "TeamRoles",
                column: "DefaultProviderId",
                principalTable: "Providers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TeamRoles_Providers_DefaultProviderId",
                table: "TeamRoles");

            migrationBuilder.DropIndex(
                name: "IX_TeamRoles_DefaultProviderId",
                table: "TeamRoles");

            migrationBuilder.DropColumn(
                name: "DefaultModelId",
                table: "TeamRoles");

            migrationBuilder.DropColumn(
                name: "DefaultProviderId",
                table: "TeamRoles");
        }
    }
}
