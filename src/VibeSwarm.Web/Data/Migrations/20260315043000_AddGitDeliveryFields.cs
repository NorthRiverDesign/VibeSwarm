using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VibeSwarm.Shared.Data;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
	/// <inheritdoc />
	[DbContext(typeof(VibeSwarmDbContext))]
	[Migration("20260315043000_AddGitDeliveryFields")]
	public partial class AddGitDeliveryFields : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<string>(
				name: "DefaultTargetBranch",
				table: "Projects",
				type: "TEXT",
				maxLength: 250,
				nullable: true);

			migrationBuilder.AddColumn<int>(
				name: "GitChangeDeliveryMode",
				table: "Projects",
				type: "INTEGER",
				nullable: false,
				defaultValue: 0);

			migrationBuilder.AddColumn<int>(
				name: "GitChangeDeliveryMode",
				table: "Jobs",
				type: "INTEGER",
				nullable: false,
				defaultValue: 0);

			migrationBuilder.AddColumn<DateTime>(
				name: "MergedAt",
				table: "Jobs",
				type: "TEXT",
				nullable: true);

			migrationBuilder.AddColumn<DateTime>(
				name: "PullRequestCreatedAt",
				table: "Jobs",
				type: "TEXT",
				nullable: true);

			migrationBuilder.AddColumn<int>(
				name: "PullRequestNumber",
				table: "Jobs",
				type: "INTEGER",
				nullable: true);

			migrationBuilder.AddColumn<string>(
				name: "PullRequestUrl",
				table: "Jobs",
				type: "TEXT",
				nullable: true);

			migrationBuilder.AddColumn<string>(
				name: "TargetBranch",
				table: "Jobs",
				type: "TEXT",
				maxLength: 250,
				nullable: true);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropColumn(
				name: "DefaultTargetBranch",
				table: "Projects");

			migrationBuilder.DropColumn(
				name: "GitChangeDeliveryMode",
				table: "Projects");

			migrationBuilder.DropColumn(
				name: "GitChangeDeliveryMode",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "MergedAt",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "PullRequestCreatedAt",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "PullRequestNumber",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "PullRequestUrl",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "TargetBranch",
				table: "Jobs");
		}
	}
}
