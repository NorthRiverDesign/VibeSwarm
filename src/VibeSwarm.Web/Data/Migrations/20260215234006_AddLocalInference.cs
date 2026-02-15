using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalInference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InferenceProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProviderType = table.Column<string>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InferenceProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InferenceModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InferenceProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ParameterSize = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Family = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    QuantizationLevel = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    TaskType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "default"),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InferenceModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InferenceModels_InferenceProviders_InferenceProviderId",
                        column: x => x.InferenceProviderId,
                        principalTable: "InferenceProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InferenceModels_InferenceProviderId_ModelId",
                table: "InferenceModels",
                columns: new[] { "InferenceProviderId", "ModelId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InferenceModels");

            migrationBuilder.DropTable(
                name: "InferenceProviders");
        }
    }
}
