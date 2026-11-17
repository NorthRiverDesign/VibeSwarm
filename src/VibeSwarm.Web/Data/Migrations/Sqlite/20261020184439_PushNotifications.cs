using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class PushNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebPushPrivateKey",
                table: "AppSettings",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebPushPublicKey",
                table: "AppSettings",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    P256dh = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Auth = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    NotifyOnCompleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyOnFailed = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyOnInputRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushSubscriptions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_UserId",
                table: "PushSubscriptions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "WebPushPrivateKey",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "WebPushPublicKey",
                table: "AppSettings");
        }
    }
}
