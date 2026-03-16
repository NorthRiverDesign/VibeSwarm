using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobGitCheckpointState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GitCheckpointBaseBranch",
                table: "Jobs",
                type: "TEXT",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitCheckpointBranch",
                table: "Jobs",
                type: "TEXT",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GitCheckpointCapturedAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitCheckpointCommitHash",
                table: "Jobs",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitCheckpointReason",
                table: "Jobs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GitCheckpointStatus",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "GitCheckpointBaseBranch", table: "Jobs");
            migrationBuilder.DropColumn(name: "GitCheckpointBranch", table: "Jobs");
            migrationBuilder.DropColumn(name: "GitCheckpointCapturedAt", table: "Jobs");
            migrationBuilder.DropColumn(name: "GitCheckpointCommitHash", table: "Jobs");
            migrationBuilder.DropColumn(name: "GitCheckpointReason", table: "Jobs");
            migrationBuilder.DropColumn(name: "GitCheckpointStatus", table: "Jobs");
        }
    }
}
