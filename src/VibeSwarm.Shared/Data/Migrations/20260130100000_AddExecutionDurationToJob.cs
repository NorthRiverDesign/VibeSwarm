using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Shared.Data.Migrations
{
	/// <inheritdoc />
	public partial class AddExecutionDurationToJob : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<double>(
				name: "ExecutionDurationSeconds",
				table: "Jobs",
				type: "REAL",
				nullable: true);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropColumn(
				name: "ExecutionDurationSeconds",
				table: "Jobs");
		}
	}
}
