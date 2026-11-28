using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
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
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "JobSchedules",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "ExecutionTarget",
                table: "JobSchedules",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "Jobs",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    DefaultProviderId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    DefaultCycleMode = table.Column<int>(type: "int", nullable: false),
                    DefaultCycleReviewPrompt = table.Column<string>(type: "longtext", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DefaultCycleSessionMode = table.Column<int>(type: "int", nullable: false),
                    DefaultMaxCycles = table.Column<int>(type: "int", nullable: false),
                    DefaultModelId = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DefaultReasoningEffort = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProfileId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Responsibilities = table.Column<string>(type: "longtext", maxLength: 4000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AgentSkills",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SkillId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ProjectAgents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AgentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ProjectId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ProviderId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PreferredModelId = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PreferredReasoningEffort = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProfileId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

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
