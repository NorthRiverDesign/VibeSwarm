using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class RunProviderJobsSideBySide : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "MaxConcurrentJobs",
                table: "Providers",
                type: "int",
                nullable: false,
                defaultValue: 5,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 1);

            // Providers still on the old default of one job at a time run jobs in separate
            // projects side by side, as new ones do. The limit was a day old, so a 1 is the default.
            migrationBuilder.Sql("UPDATE Providers SET MaxConcurrentJobs = 5 WHERE MaxConcurrentJobs = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Providers SET MaxConcurrentJobs = 1 WHERE MaxConcurrentJobs = 5;");

            migrationBuilder.AlterColumn<int>(
                name: "MaxConcurrentJobs",
                table: "Providers",
                type: "int",
                nullable: false,
                defaultValue: 1,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 5);
        }
    }
}
