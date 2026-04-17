using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobScheduleIntervalMinutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IntervalMinutes",
                table: "JobSchedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IntervalMinutes",
                table: "JobSchedules");
        }
    }
}
