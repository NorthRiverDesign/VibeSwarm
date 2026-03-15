using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VibeSwarm.Shared.Data;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
	/// <inheritdoc />
	[DbContext(typeof(VibeSwarmDbContext))]
	[Migration("20260315020900_AddProjectEnvironmentStage")]
	public partial class AddProjectEnvironmentStage : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<string>(
				name: "Stage",
				table: "ProjectEnvironments",
				type: "TEXT",
				nullable: false,
				defaultValue: "Production");
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropColumn(
				name: "Stage",
				table: "ProjectEnvironments");
		}
	}
}
