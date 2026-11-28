using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class RemoveAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_Agents_AgentId",
                table: "Jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_JobSchedules_Agents_AgentId",
                table: "JobSchedules");

            migrationBuilder.DropTable(
                name: "AgentSkills");

            migrationBuilder.DropTable(
                name: "ProjectAgents");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropIndex(
                name: "IX_JobSchedules_AgentId",
                table: "JobSchedules");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_AgentId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "EnableTeamSwarm",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "JobSchedules");

            migrationBuilder.DropColumn(
                name: "ExecutionTarget",
                table: "JobSchedules");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "Jobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableTeamSwarm",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "JobSchedules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionTarget",
                table: "JobSchedules",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DefaultProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DefaultCycleMode = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultCycleReviewPrompt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DefaultCycleSessionMode = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultMaxCycles = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DefaultReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Responsibilities = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Agents_Providers_DefaultProviderId",
                        column: x => x.DefaultProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AgentSkills",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SkillId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSkills", x => new { x.AgentId, x.SkillId });
                    table.ForeignKey(
                        name: "FK_AgentSkills_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentSkills_Skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectAgents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreferredModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PreferredReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectAgents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectAgents_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectAgents_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectAgents_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobSchedules_AgentId",
                table: "JobSchedules",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_AgentId",
                table: "Jobs",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_DefaultProviderId",
                table: "Agents",
                column: "DefaultProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_Name",
                table: "Agents",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSkills_SkillId",
                table: "AgentSkills",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectAgents_AgentId",
                table: "ProjectAgents",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectAgents_ProjectId_AgentId",
                table: "ProjectAgents",
                columns: new[] { "ProjectId", "AgentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectAgents_ProviderId",
                table: "ProjectAgents",
                column: "ProviderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Agents_AgentId",
                table: "Jobs",
                column: "AgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_JobSchedules_Agents_AgentId",
                table: "JobSchedules",
                column: "AgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
