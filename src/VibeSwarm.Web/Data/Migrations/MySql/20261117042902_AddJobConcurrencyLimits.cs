using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class AddJobConcurrencyLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentJobs",
                table: "Providers",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentJobs",
                table: "AppSettings",
                type: "int",
                nullable: false,
                defaultValue: 5);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxConcurrentJobs",
                table: "Providers");

            migrationBuilder.DropColumn(
                name: "MaxConcurrentJobs",
                table: "AppSettings");
        }
    }
}
