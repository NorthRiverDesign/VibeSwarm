using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Shared.Data.Migrations
{
	/// <inheritdoc />
	public partial class AddMaxExecutionToProvider : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<int>(
				name: "MaxExecutionMinutes",
				table: "Providers",
				type: "INTEGER",
				nullable: true);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropColumn(
				name: "MaxExecutionMinutes",
				table: "Providers");
		}
	}
}
