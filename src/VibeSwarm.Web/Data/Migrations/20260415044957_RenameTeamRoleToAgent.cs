using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTeamRoleToAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename columns in Jobs and JobSchedules first (they reference the old table via FK)
            migrationBuilder.RenameColumn(
                name: "TeamRoleId",
                table: "Jobs",
                newName: "AgentId");

            migrationBuilder.RenameIndex(
                name: "IX_Jobs_TeamRoleId",
                table: "Jobs",
                newName: "IX_Jobs_AgentId");

            migrationBuilder.RenameColumn(
                name: "TeamRoleId",
                table: "JobSchedules",
                newName: "AgentId");

            migrationBuilder.RenameIndex(
                name: "IX_JobSchedules_TeamRoleId",
                table: "JobSchedules",
                newName: "IX_JobSchedules_AgentId");

            // Rename the AgentSkills table and its TeamRoleId column
            migrationBuilder.RenameTable(
                name: "TeamRoleSkills",
                newName: "AgentSkills");

            migrationBuilder.RenameColumn(
                name: "TeamRoleId",
                table: "AgentSkills",
                newName: "AgentId");

            migrationBuilder.RenameIndex(
                name: "IX_TeamRoleSkills_SkillId",
                table: "AgentSkills",
                newName: "IX_AgentSkills_SkillId");

            // Rename the ProjectAgents table and its TeamRoleId column
            migrationBuilder.RenameTable(
                name: "ProjectTeamRoles",
                newName: "ProjectAgents");

            migrationBuilder.RenameColumn(
                name: "TeamRoleId",
                table: "ProjectAgents",
                newName: "AgentId");

            migrationBuilder.RenameIndex(
                name: "IX_ProjectTeamRoles_TeamRoleId",
                table: "ProjectAgents",
                newName: "IX_ProjectAgents_AgentId");

            migrationBuilder.RenameIndex(
                name: "IX_ProjectTeamRoles_ProjectId_TeamRoleId",
                table: "ProjectAgents",
                newName: "IX_ProjectAgents_ProjectId_AgentId");

            migrationBuilder.RenameIndex(
                name: "IX_ProjectTeamRoles_ProviderId",
                table: "ProjectAgents",
                newName: "IX_ProjectAgents_ProviderId");

            // Rename the main Agents table
            migrationBuilder.RenameTable(
                name: "TeamRoles",
                newName: "Agents");

            migrationBuilder.RenameIndex(
                name: "IX_TeamRoles_DefaultProviderId",
                table: "Agents",
                newName: "IX_Agents_DefaultProviderId");

            migrationBuilder.RenameIndex(
                name: "IX_TeamRoles_Name",
                table: "Agents",
                newName: "IX_Agents_Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "Agents",
                newName: "TeamRoles");

            migrationBuilder.RenameIndex(
                name: "IX_Agents_DefaultProviderId",
                table: "TeamRoles",
                newName: "IX_TeamRoles_DefaultProviderId");

            migrationBuilder.RenameIndex(
                name: "IX_Agents_Name",
                table: "TeamRoles",
                newName: "IX_TeamRoles_Name");

            migrationBuilder.RenameTable(
                name: "AgentSkills",
                newName: "TeamRoleSkills");

            migrationBuilder.RenameColumn(
                name: "AgentId",
                table: "TeamRoleSkills",
                newName: "TeamRoleId");

            migrationBuilder.RenameIndex(
                name: "IX_AgentSkills_SkillId",
                table: "TeamRoleSkills",
                newName: "IX_TeamRoleSkills_SkillId");

            migrationBuilder.RenameTable(
                name: "ProjectAgents",
                newName: "ProjectTeamRoles");

            migrationBuilder.RenameColumn(
                name: "AgentId",
                table: "ProjectTeamRoles",
                newName: "TeamRoleId");

            migrationBuilder.RenameIndex(
                name: "IX_ProjectAgents_AgentId",
                table: "ProjectTeamRoles",
                newName: "IX_ProjectTeamRoles_TeamRoleId");

            migrationBuilder.RenameIndex(
                name: "IX_ProjectAgents_ProjectId_AgentId",
                table: "ProjectTeamRoles",
                newName: "IX_ProjectTeamRoles_ProjectId_TeamRoleId");

            migrationBuilder.RenameIndex(
                name: "IX_ProjectAgents_ProviderId",
                table: "ProjectTeamRoles",
                newName: "IX_ProjectTeamRoles_ProviderId");

            migrationBuilder.RenameColumn(
                name: "AgentId",
                table: "Jobs",
                newName: "TeamRoleId");

            migrationBuilder.RenameIndex(
                name: "IX_Jobs_AgentId",
                table: "Jobs",
                newName: "IX_Jobs_TeamRoleId");

            migrationBuilder.RenameColumn(
                name: "AgentId",
                table: "JobSchedules",
                newName: "TeamRoleId");

            migrationBuilder.RenameIndex(
                name: "IX_JobSchedules_AgentId",
                table: "JobSchedules",
                newName: "IX_JobSchedules_TeamRoleId");
        }
    }
}
