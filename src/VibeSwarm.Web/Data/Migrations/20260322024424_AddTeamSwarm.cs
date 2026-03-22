using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamSwarm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableTeamSwarm",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SwarmId",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TeamRoleId",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_SwarmId",
                table: "Jobs",
                column: "SwarmId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TeamRoleId",
                table: "Jobs",
                column: "TeamRoleId");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_TeamRoles_TeamRoleId",
                table: "Jobs",
                column: "TeamRoleId",
                principalTable: "TeamRoles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_TeamRoles_TeamRoleId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_SwarmId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_TeamRoleId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "EnableTeamSwarm",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "SwarmId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "TeamRoleId",
                table: "Jobs");
        }
    }
}
