using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableJobRecoveryAndProviderCooldowns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveRateLimitCount",
                table: "ProviderUsageSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastJobStartedAt",
                table: "ProviderUsageSummaries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRateLimitAt",
                table: "ProviderUsageSummaries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastRateLimitMessage",
                table: "ProviderUsageSummaries",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextExecutionAvailableAt",
                table: "ProviderUsageSummaries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ForceFreshSession",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastResumeAttemptAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastResumeFailureReason",
                table: "Jobs",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecoveryCheckpointAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryPrompt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResumeAttemptCount",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ResumeFromStatus",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsecutiveRateLimitCount",
                table: "ProviderUsageSummaries");

            migrationBuilder.DropColumn(
                name: "LastJobStartedAt",
                table: "ProviderUsageSummaries");

            migrationBuilder.DropColumn(
                name: "LastRateLimitAt",
                table: "ProviderUsageSummaries");

            migrationBuilder.DropColumn(
                name: "LastRateLimitMessage",
                table: "ProviderUsageSummaries");

            migrationBuilder.DropColumn(
                name: "NextExecutionAvailableAt",
                table: "ProviderUsageSummaries");

            migrationBuilder.DropColumn(
                name: "ForceFreshSession",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "LastResumeAttemptAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "LastResumeFailureReason",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RecoveryCheckpointAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RecoveryPrompt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ResumeAttemptCount",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ResumeFromStatus",
                table: "Jobs");
        }
    }
}
