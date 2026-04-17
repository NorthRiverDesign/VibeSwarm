using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixAgentDefaultMaxCyclesFloor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The AddTeamRoleExecutionDefaults migration used defaultValue: 0 for DefaultMaxCycles,
            // but the Agent model requires [Range(1, 100)]. Fix any existing agents at 0.
            migrationBuilder.Sql("UPDATE \"Agents\" SET \"DefaultMaxCycles\" = 1 WHERE \"DefaultMaxCycles\" < 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
