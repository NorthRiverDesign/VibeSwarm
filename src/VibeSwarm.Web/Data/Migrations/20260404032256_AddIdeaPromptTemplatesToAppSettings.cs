using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIdeaPromptTemplatesToAppSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovedIdeaImplementationPromptTemplate",
                table: "AppSettings",
                type: "TEXT",
                maxLength: 12000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdeaExpansionPromptTemplate",
                table: "AppSettings",
                type: "TEXT",
                maxLength: 12000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdeaImplementationPromptTemplate",
                table: "AppSettings",
                type: "TEXT",
                maxLength: 12000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedIdeaImplementationPromptTemplate",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "IdeaExpansionPromptTemplate",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "IdeaImplementationPromptTemplate",
                table: "AppSettings");
        }
    }
}
