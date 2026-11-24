using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class ClearProjectSettingsFromTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every template saved before templates ran across projects copied one project's branch,
            // pull-request target and delivery mode from the job form. Templates now leave those to
            // the project they run on, so clear the copies rather than push every project onto one
            // branch. (A separate migration from the column change: SQLite rebuilds the table at the
            // end of a migration, after any SQL in it.)
            migrationBuilder.Sql("UPDATE JobTemplates SET Branch = NULL, TargetBranch = NULL, GitChangeDeliveryMode = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
