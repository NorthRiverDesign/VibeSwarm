using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixLegacyStringEnumValues : Migration
    {
        /// <summary>
        /// Earlier schema stored enums as strings; current config expects integers.
        /// Convert any remaining string values to their correct integer equivalents.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE Jobs SET Status = 0 WHERE Status = 'New';
                UPDATE Jobs SET Status = 1 WHERE Status = 'Pending';
                UPDATE Jobs SET Status = 2 WHERE Status = 'Started';
                UPDATE Jobs SET Status = 3 WHERE Status = 'Processing';
                UPDATE Jobs SET Status = 4 WHERE Status = 'Completed';
                UPDATE Jobs SET Status = 5 WHERE Status = 'Failed';
                UPDATE Jobs SET Status = 6 WHERE Status = 'Cancelled';
                UPDATE Jobs SET Status = 7 WHERE Status = 'Stalled';
                UPDATE Jobs SET CycleMode = 0 WHERE CycleMode = 'SingleCycle';
                UPDATE Jobs SET CycleMode = 1 WHERE CycleMode = 'FixedCount';
                UPDATE Jobs SET CycleSessionMode = 0 WHERE CycleSessionMode = 'ContinueSession';
                UPDATE Jobs SET CycleSessionMode = 1 WHERE CycleSessionMode = 'FreshSession';
                UPDATE Projects SET AutoCommitMode = 0 WHERE AutoCommitMode = 'Off';
                UPDATE Projects SET AutoCommitMode = 1 WHERE AutoCommitMode = 'CommitOnly';
                UPDATE Projects SET AutoCommitMode = 2 WHERE AutoCommitMode = 'CommitAndPush';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
