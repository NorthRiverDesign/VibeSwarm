using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddRunnerProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IdeasProcessingProfileId",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProfileId",
                table: "JobSchedules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProfileId",
                table: "IterationLoops",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdeasProcessingProfileId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "JobSchedules");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "IterationLoops");
        }
    }
}
