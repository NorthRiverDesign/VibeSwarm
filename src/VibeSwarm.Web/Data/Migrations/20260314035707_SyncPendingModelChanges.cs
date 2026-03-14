using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncPendingModelChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProjectProviders_Providers_ProviderId",
                table: "ProjectProviders");

            migrationBuilder.DropIndex(
                name: "IX_ProviderUsageRecords_ProviderId_RecordedAt",
                table: "ProviderUsageRecords");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_DependsOnJobId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_ParentJobId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_Status_Priority_CreatedAt",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_JobProviderAttempts_JobId_AttemptedAt",
                table: "JobProviderAttempts");

            migrationBuilder.DropIndex(
                name: "IX_JobProviderAttempts_JobId_AttemptOrder",
                table: "JobProviderAttempts");

            migrationBuilder.DropIndex(
                name: "IX_InferenceModels_InferenceProviderId_ModelId",
                table: "InferenceModels");

            migrationBuilder.DropIndex(
                name: "IX_Ideas_ProjectId_SortOrder",
                table: "Ideas");

            migrationBuilder.AlterColumn<bool>(
                name: "IsActive",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: true);

            migrationBuilder.AlterColumn<int>(
                name: "AutoCommitMode",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "Off");

            migrationBuilder.AlterColumn<int>(
                name: "Priority",
                table: "ProjectProviders",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<bool>(
                name: "IsEnabled",
                table: "ProjectProviders",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: true);

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<int>(
                name: "MaxCycles",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldDefaultValue: 1);

            migrationBuilder.AlterColumn<int>(
                name: "CycleSessionMode",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "ContinueSession");

            migrationBuilder.AlterColumn<int>(
                name: "CycleMode",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "SingleCycle");

            migrationBuilder.AlterColumn<int>(
                name: "CurrentCycle",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldDefaultValue: 1);

            migrationBuilder.AlterColumn<int>(
                name: "ActiveExecutionIndex",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "TaskType",
                table: "InferenceModels",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 100,
                oldDefaultValue: "default");

            migrationBuilder.AlterColumn<string>(
                name: "ExpansionStatus",
                table: "Ideas",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageRecords_ProviderId",
                table: "ProviderUsageRecords",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageRecords_RecordedAt",
                table: "ProviderUsageRecords",
                column: "RecordedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Providers_Name",
                table: "Providers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Name",
                table: "Projects",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CreatedAt",
                table: "Jobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_JobProviderAttempts_JobId",
                table: "JobProviderAttempts",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobProviderAttempts_JobId_AttemptOrder",
                table: "JobProviderAttempts",
                columns: new[] { "JobId", "AttemptOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_JobMessages_CreatedAt",
                table: "JobMessages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InferenceProviders_Name",
                table: "InferenceProviders",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InferenceModels_InferenceProviderId_ModelId_TaskType",
                table: "InferenceModels",
                columns: new[] { "InferenceProviderId", "ModelId", "TaskType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ideas_CreatedAt",
                table: "Ideas",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Ideas_ProjectId",
                table: "Ideas",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Ideas_SortOrder",
                table: "Ideas",
                column: "SortOrder");

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectProviders_Providers_ProviderId",
                table: "ProjectProviders",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProjectProviders_Providers_ProviderId",
                table: "ProjectProviders");

            migrationBuilder.DropIndex(
                name: "IX_ProviderUsageRecords_ProviderId",
                table: "ProviderUsageRecords");

            migrationBuilder.DropIndex(
                name: "IX_ProviderUsageRecords_RecordedAt",
                table: "ProviderUsageRecords");

            migrationBuilder.DropIndex(
                name: "IX_Providers_Name",
                table: "Providers");

            migrationBuilder.DropIndex(
                name: "IX_Projects_Name",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_CreatedAt",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_JobProviderAttempts_JobId",
                table: "JobProviderAttempts");

            migrationBuilder.DropIndex(
                name: "IX_JobProviderAttempts_JobId_AttemptOrder",
                table: "JobProviderAttempts");

            migrationBuilder.DropIndex(
                name: "IX_JobMessages_CreatedAt",
                table: "JobMessages");

            migrationBuilder.DropIndex(
                name: "IX_InferenceProviders_Name",
                table: "InferenceProviders");

            migrationBuilder.DropIndex(
                name: "IX_InferenceModels_InferenceProviderId_ModelId_TaskType",
                table: "InferenceModels");

            migrationBuilder.DropIndex(
                name: "IX_Ideas_CreatedAt",
                table: "Ideas");

            migrationBuilder.DropIndex(
                name: "IX_Ideas_ProjectId",
                table: "Ideas");

            migrationBuilder.DropIndex(
                name: "IX_Ideas_SortOrder",
                table: "Ideas");

            migrationBuilder.AlterColumn<bool>(
                name: "IsActive",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "AutoCommitMode",
                table: "Projects",
                type: "TEXT",
                nullable: false,
                defaultValue: "Off",
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "Priority",
                table: "ProjectProviders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<bool>(
                name: "IsEnabled",
                table: "ProjectProviders",
                type: "INTEGER",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Jobs",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "MaxCycles",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "CycleSessionMode",
                table: "Jobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "ContinueSession",
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "CycleMode",
                table: "Jobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "SingleCycle",
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "CurrentCycle",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "ActiveExecutionIndex",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "TaskType",
                table: "InferenceModels",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "default",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<int>(
                name: "ExpansionStatus",
                table: "Ideas",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageRecords_ProviderId_RecordedAt",
                table: "ProviderUsageRecords",
                columns: new[] { "ProviderId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_DependsOnJobId",
                table: "Jobs",
                column: "DependsOnJobId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ParentJobId",
                table: "Jobs",
                column: "ParentJobId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status_Priority_CreatedAt",
                table: "Jobs",
                columns: new[] { "Status", "Priority", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_JobProviderAttempts_JobId_AttemptedAt",
                table: "JobProviderAttempts",
                columns: new[] { "JobId", "AttemptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_JobProviderAttempts_JobId_AttemptOrder",
                table: "JobProviderAttempts",
                columns: new[] { "JobId", "AttemptOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InferenceModels_InferenceProviderId_ModelId",
                table: "InferenceModels",
                columns: new[] { "InferenceProviderId", "ModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ideas_ProjectId_SortOrder",
                table: "Ideas",
                columns: new[] { "ProjectId", "SortOrder" });

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectProviders_Providers_ProviderId",
                table: "ProjectProviders",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
