using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddJobWorkSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkSnapshotCommit",
                table: "Jobs",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitDiff",
                table: "JobChangeSets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkSnapshotCommit",
                table: "JobChangeSets",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkSnapshotCommit",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "GitDiff",
                table: "JobChangeSets");

            migrationBuilder.DropColumn(
                name: "WorkSnapshotCommit",
                table: "JobChangeSets");
        }
    }
}
