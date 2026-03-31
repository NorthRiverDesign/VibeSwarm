using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleExecutionTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JobSchedules_Providers_ProviderId",
                table: "JobSchedules");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProviderId",
                table: "JobSchedules",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "ExecutionTarget",
                table: "JobSchedules",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "TeamRoleId",
                table: "JobSchedules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobSchedules_TeamRoleId",
                table: "JobSchedules",
                column: "TeamRoleId");

            migrationBuilder.AddForeignKey(
                name: "FK_JobSchedules_Providers_ProviderId",
                table: "JobSchedules",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_JobSchedules_TeamRoles_TeamRoleId",
                table: "JobSchedules",
                column: "TeamRoleId",
                principalTable: "TeamRoles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JobSchedules_Providers_ProviderId",
                table: "JobSchedules");

            migrationBuilder.DropForeignKey(
                name: "FK_JobSchedules_TeamRoles_TeamRoleId",
                table: "JobSchedules");

            migrationBuilder.DropIndex(
                name: "IX_JobSchedules_TeamRoleId",
                table: "JobSchedules");

            migrationBuilder.DropColumn(
                name: "ExecutionTarget",
                table: "JobSchedules");

            migrationBuilder.DropColumn(
                name: "TeamRoleId",
                table: "JobSchedules");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProviderId",
                table: "JobSchedules",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_JobSchedules_Providers_ProviderId",
                table: "JobSchedules",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
