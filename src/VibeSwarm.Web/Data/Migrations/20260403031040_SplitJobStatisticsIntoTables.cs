using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitJobStatisticsIntoTables : Migration
    {
        /// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.CreateTable(
				name: "JobExecutionStatistics",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CostUsd = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobExecutionStatistics", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_JobExecutionStatistics_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobPlanningStatistics",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CostUsd = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobPlanningStatistics", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_JobPlanningStatistics_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobStatistics",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionDurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    TotalCostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobStatistics", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_JobStatistics_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
					principalColumn: "Id",
					onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.Sql(
				"""
				INSERT INTO "JobStatistics" ("JobId", "ExecutionDurationSeconds", "TotalCostUsd", "InputTokens", "OutputTokens")
				SELECT "Id", "ExecutionDurationSeconds", "TotalCostUsd", "InputTokens", "OutputTokens"
				FROM "Jobs"
				WHERE "ExecutionDurationSeconds" IS NOT NULL
					OR "TotalCostUsd" IS NOT NULL
					OR "InputTokens" IS NOT NULL
					OR "OutputTokens" IS NOT NULL;
				""");

			migrationBuilder.Sql(
				"""
				INSERT INTO "JobPlanningStatistics" ("JobId", "InputTokens", "OutputTokens", "CostUsd")
				SELECT "Id", "PlanningInputTokens", "PlanningOutputTokens", "PlanningCostUsd"
				FROM "Jobs"
				WHERE "PlanningInputTokens" IS NOT NULL
					OR "PlanningOutputTokens" IS NOT NULL
					OR "PlanningCostUsd" IS NOT NULL;
				""");

			migrationBuilder.Sql(
				"""
				INSERT INTO "JobExecutionStatistics" ("JobId", "InputTokens", "OutputTokens", "CostUsd")
				SELECT "Id", "ExecutionInputTokens", "ExecutionOutputTokens", "ExecutionCostUsd"
				FROM "Jobs"
				WHERE "ExecutionInputTokens" IS NOT NULL
					OR "ExecutionOutputTokens" IS NOT NULL
					OR "ExecutionCostUsd" IS NOT NULL;
				""");

			migrationBuilder.DropColumn(
				name: "ExecutionCostUsd",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "ExecutionDurationSeconds",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "ExecutionInputTokens",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "ExecutionOutputTokens",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "InputTokens",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "OutputTokens",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "PlanningCostUsd",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "PlanningInputTokens",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "PlanningOutputTokens",
				table: "Jobs");

			migrationBuilder.DropColumn(
				name: "TotalCostUsd",
				table: "Jobs");
		}

        /// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<decimal>(
				name: "ExecutionCostUsd",
				table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ExecutionDurationSeconds",
                table: "Jobs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExecutionInputTokens",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExecutionOutputTokens",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PlanningCostUsd",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlanningInputTokens",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlanningOutputTokens",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

			migrationBuilder.AddColumn<decimal>(
				name: "TotalCostUsd",
				table: "Jobs",
				type: "TEXT",
				nullable: true);

			migrationBuilder.Sql(
				"""
				UPDATE "Jobs"
				SET "ExecutionDurationSeconds" = (
						SELECT "ExecutionDurationSeconds"
						FROM "JobStatistics"
						WHERE "JobStatistics"."JobId" = "Jobs"."Id"
					),
					"TotalCostUsd" = (
						SELECT "TotalCostUsd"
						FROM "JobStatistics"
						WHERE "JobStatistics"."JobId" = "Jobs"."Id"
					),
					"InputTokens" = (
						SELECT "InputTokens"
						FROM "JobStatistics"
						WHERE "JobStatistics"."JobId" = "Jobs"."Id"
					),
					"OutputTokens" = (
						SELECT "OutputTokens"
						FROM "JobStatistics"
						WHERE "JobStatistics"."JobId" = "Jobs"."Id"
					),
					"PlanningInputTokens" = (
						SELECT "InputTokens"
						FROM "JobPlanningStatistics"
						WHERE "JobPlanningStatistics"."JobId" = "Jobs"."Id"
					),
					"PlanningOutputTokens" = (
						SELECT "OutputTokens"
						FROM "JobPlanningStatistics"
						WHERE "JobPlanningStatistics"."JobId" = "Jobs"."Id"
					),
					"PlanningCostUsd" = (
						SELECT "CostUsd"
						FROM "JobPlanningStatistics"
						WHERE "JobPlanningStatistics"."JobId" = "Jobs"."Id"
					),
					"ExecutionInputTokens" = (
						SELECT "InputTokens"
						FROM "JobExecutionStatistics"
						WHERE "JobExecutionStatistics"."JobId" = "Jobs"."Id"
					),
					"ExecutionOutputTokens" = (
						SELECT "OutputTokens"
						FROM "JobExecutionStatistics"
						WHERE "JobExecutionStatistics"."JobId" = "Jobs"."Id"
					),
					"ExecutionCostUsd" = (
						SELECT "CostUsd"
						FROM "JobExecutionStatistics"
						WHERE "JobExecutionStatistics"."JobId" = "Jobs"."Id"
					);
				""");

			migrationBuilder.DropTable(
				name: "JobExecutionStatistics");

			migrationBuilder.DropTable(
				name: "JobPlanningStatistics");

			migrationBuilder.DropTable(
				name: "JobStatistics");
		}
	}
}
