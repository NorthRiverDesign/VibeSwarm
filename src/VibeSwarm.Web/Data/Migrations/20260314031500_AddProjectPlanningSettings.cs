using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
	/// <inheritdoc />
	public partial class AddProjectPlanningSettings : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<bool>(
				name: "PlanningEnabled",
				table: "Projects",
				type: "INTEGER",
				nullable: false,
				defaultValue: false);

			migrationBuilder.AddColumn<string>(
				name: "PlanningModelId",
				table: "Projects",
				type: "TEXT",
				maxLength: 200,
				nullable: true);

			migrationBuilder.AddColumn<Guid>(
				name: "PlanningProviderId",
				table: "Projects",
				type: "TEXT",
				nullable: true);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropColumn(
				name: "PlanningEnabled",
				table: "Projects");

			migrationBuilder.DropColumn(
				name: "PlanningModelId",
				table: "Projects");

			migrationBuilder.DropColumn(
				name: "PlanningProviderId",
				table: "Projects");
		}
	}
}
