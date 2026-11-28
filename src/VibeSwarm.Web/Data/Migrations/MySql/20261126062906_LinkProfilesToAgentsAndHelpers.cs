using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class LinkProfilesToAgentsAndHelpers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DecisionsProfileId",
                table: "Projects",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "ResearchProfileId",
                table: "Projects",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "ProfileId",
                table: "ProjectAgents",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProviderId",
                table: "Profiles",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "InferenceProviderId",
                table: "Profiles",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<bool>(
                name: "IsProviderDefault",
                table: "Profiles",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ProfileId",
                table: "Agents",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_InferenceProviderId",
                table: "Profiles",
                column: "InferenceProviderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Profiles_InferenceProviders_InferenceProviderId",
                table: "Profiles",
                column: "InferenceProviderId",
                principalTable: "InferenceProviders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Profiles_InferenceProviders_InferenceProviderId",
                table: "Profiles");

            migrationBuilder.DropIndex(
                name: "IX_Profiles_InferenceProviderId",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "DecisionsProfileId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ResearchProfileId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "ProjectAgents");

            migrationBuilder.DropColumn(
                name: "InferenceProviderId",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "IsProviderDefault",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "Agents");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProviderId",
                table: "Profiles",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");
        }
    }
}
