using System;
using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutomaticTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationAlert_Kind",
                schema: "projects",
                table: "LocationAlert");

            migrationBuilder.AddColumn<Guid>(
                name: "ClientPointId",
                schema: "projects",
                table: "LocationPing",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IdempotencyKey",
                schema: "projects",
                table: "DailyReport",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WrittenAt",
                schema: "projects",
                table: "DailyReport",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LocationDevice",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    DeviceId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Platform = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    AppVersion = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    PolicyVersion = table.Column<int>(type: "int", nullable: false),
                    PolicyAcceptedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LastHeartbeatAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastHeartbeatReceivedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    BatteryPercent = table.Column<int>(type: "int", nullable: true),
                    BatteryCharging = table.Column<bool>(type: "bit", nullable: true),
                    TrackingState = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    Permission = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    GpsEnabled = table.Column<bool>(type: "bit", nullable: true),
                    PendingPoints = table.Column<int>(type: "int", nullable: true),
                    LowBatterySince = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationDevice", x => x.Id);
                    table.CheckConstraint("CK_LocationDevice_Battery", "[BatteryPercent] IS NULL OR [BatteryPercent] BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_LocationDevice_PendingPoints", "[PendingPoints] IS NULL OR [PendingPoints] >= 0");
                    table.CheckConstraint("CK_LocationDevice_Permission", "[Permission] IS NULL OR [Permission] IN ('Always', 'WhileInUse', 'Denied')");
                    table.CheckConstraint("CK_LocationDevice_TrackingState", "[TrackingState] IS NULL OR [TrackingState] IN ('Running', 'PermissionDenied', 'BackgroundDenied', 'GpsOff', 'Stopped')");
                });

            migrationBuilder.CreateIndex(
                name: "UX_LocationPing_User_ClientPoint_Project",
                schema: "projects",
                table: "LocationPing",
                columns: new[] { "UserId", "ClientPointId", "ProjectId" },
                unique: true,
                filter: "[ClientPointId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationAlert_Kind",
                schema: "projects",
                table: "LocationAlert",
                sql: "[Kind] IN ('Unavailable', 'OutsideSite', 'LowBattery')");

            migrationBuilder.CreateIndex(
                name: "UX_DailyReport_Author_IdempotencyKey",
                schema: "projects",
                table: "DailyReport",
                columns: new[] { "AuthorUserId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_LocationDevice_User_Device",
                schema: "projects",
                table: "LocationDevice",
                columns: new[] { "UserId", "DeviceId" },
                unique: true);

            // The user is an Access record: this key is added by hand (Access owns the table).
            migrationBuilder.AddForeignKey(
                name: LocationDeviceConfiguration.UserForeignKey,
                schema: "projects",
                table: "LocationDevice",
                column: "UserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocationDevice",
                schema: "projects");

            migrationBuilder.DropIndex(
                name: "UX_LocationPing_User_ClientPoint_Project",
                schema: "projects",
                table: "LocationPing");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationAlert_Kind",
                schema: "projects",
                table: "LocationAlert");

            migrationBuilder.DropIndex(
                name: "UX_DailyReport_Author_IdempotencyKey",
                schema: "projects",
                table: "DailyReport");

            migrationBuilder.DropColumn(
                name: "ClientPointId",
                schema: "projects",
                table: "LocationPing");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "projects",
                table: "DailyReport");

            migrationBuilder.DropColumn(
                name: "WrittenAt",
                schema: "projects",
                table: "DailyReport");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationAlert_Kind",
                schema: "projects",
                table: "LocationAlert",
                sql: "[Kind] IN ('Unavailable', 'OutsideSite')");
        }
    }
}
