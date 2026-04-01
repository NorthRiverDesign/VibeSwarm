using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledIdeaGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IdeaCount",
                table: "JobSchedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<Guid>(
                name: "InferenceProviderId",
                table: "JobSchedules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleType",
                table: "JobSchedules",
                type: "TEXT",
                nullable: false,
                defaultValue: "RunJob");

            migrationBuilder.CreateIndex(
                name: "IX_JobSchedules_InferenceProviderId",
                table: "JobSchedules",
                column: "InferenceProviderId");

            migrationBuilder.AddForeignKey(
                name: "FK_JobSchedules_InferenceProviders_InferenceProviderId",
                table: "JobSchedules",
                column: "InferenceProviderId",
                principalTable: "InferenceProviders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JobSchedules_InferenceProviders_InferenceProviderId",
                table: "JobSchedules");

            migrationBuilder.DropIndex(
                name: "IX_JobSchedules_InferenceProviderId",
                table: "JobSchedules");

            migrationBuilder.DropColumn(
                name: "IdeaCount",
                table: "JobSchedules");

            migrationBuilder.DropColumn(
                name: "InferenceProviderId",
                table: "JobSchedules");

            migrationBuilder.DropColumn(
                name: "ScheduleType",
                table: "JobSchedules");
        }
    }
}
