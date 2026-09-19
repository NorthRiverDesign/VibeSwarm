using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class AddJobQueuePause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "JobQueuePaused",
                table: "AppSettings",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "JobQueuePausedAt",
                table: "AppSettings",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobQueuePausedReason",
                table: "AppSettings",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JobQueuePaused",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "JobQueuePausedAt",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "JobQueuePausedReason",
                table: "AppSettings");
        }
    }
}
