using System;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Fleet.Data.Migrations
{
    /// <inheritdoc />
    internal partial class VehicleGpsTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_VehicleExpense_Type",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.AddColumn<bool>(
                name: "TrackingEnabled",
                schema: "fleet",
                table: "Vehicle",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "VehicleTrackingAlert",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VehicleId = table.Column<int>(type: "int", nullable: false),
                    DriverEmployeeId = table.Column<int>(type: "int", nullable: true),
                    RecipientUserId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    LastPointAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleTrackingAlert", x => x.Id);
                    table.CheckConstraint("CK_VehicleTrackingAlert_Status", "[Status] IN ('Open', 'Recovered', 'Ended')");
                    table.ForeignKey(
                        name: "FK_VehicleTrackingAlert_Vehicle_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicle",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VehicleTrackingDevice",
                schema: "fleet",
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
                    LastUploadAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    BatteryPercent = table.Column<int>(type: "int", nullable: true),
                    BatteryCharging = table.Column<bool>(type: "bit", nullable: true),
                    TrackingState = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    Permission = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    GpsEnabled = table.Column<bool>(type: "bit", nullable: true),
                    PendingPoints = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleTrackingDevice", x => x.Id);
                    table.CheckConstraint("CK_VehicleTrackingDevice_Battery", "[BatteryPercent] IS NULL OR [BatteryPercent] BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_VehicleTrackingDevice_Pending", "[PendingPoints] IS NULL OR [PendingPoints] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "VehicleTrackingState",
                schema: "fleet",
                columns: table => new
                {
                    VehicleId = table.Column<int>(type: "int", nullable: false),
                    LastPointAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastReceivedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastLatitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    LastLongitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    LastAccuracyMeters = table.Column<int>(type: "int", nullable: true),
                    LastSpeedKmh = table.Column<decimal>(type: "decimal(6,1)", precision: 6, scale: 1, nullable: true),
                    LastDriverEmployeeId = table.Column<int>(type: "int", nullable: true),
                    SilentSince = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleTrackingState", x => x.VehicleId);
                    table.CheckConstraint("CK_VehicleTrackingState_Position", "([LastLatitude] IS NULL AND [LastLongitude] IS NULL) OR ([LastLatitude] BETWEEN -90 AND 90 AND [LastLongitude] BETWEEN -180 AND 180)");
                    table.ForeignKey(
                        name: "FK_VehicleTrackingState_Vehicle_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicle",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VehicleGpsPoint",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VehicleId = table.Column<int>(type: "int", nullable: false),
                    AssignmentId = table.Column<int>(type: "int", nullable: false),
                    DriverEmployeeId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    DeviceId = table.Column<int>(type: "int", nullable: false),
                    ClientPointId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    AccuracyMeters = table.Column<int>(type: "int", nullable: true),
                    SpeedKmh = table.Column<decimal>(type: "decimal(6,1)", precision: 6, scale: 1, nullable: true),
                    HeadingDegrees = table.Column<short>(type: "smallint", nullable: true),
                    AltitudeMeters = table.Column<decimal>(type: "decimal(7,1)", precision: 7, scale: 1, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleGpsPoint", x => x.Id);
                    table.CheckConstraint("CK_VehicleGpsPoint_Accuracy", "[AccuracyMeters] IS NULL OR [AccuracyMeters] >= 0");
                    table.CheckConstraint("CK_VehicleGpsPoint_Heading", "[HeadingDegrees] IS NULL OR [HeadingDegrees] BETWEEN 0 AND 360");
                    table.CheckConstraint("CK_VehicleGpsPoint_Position", "[Latitude] BETWEEN -90 AND 90 AND [Longitude] BETWEEN -180 AND 180");
                    table.CheckConstraint("CK_VehicleGpsPoint_Speed", "[SpeedKmh] IS NULL OR [SpeedKmh] >= 0");
                    table.ForeignKey(
                        name: "FK_VehicleGpsPoint_VehicleDriverAssignment_AssignmentId",
                        column: x => x.AssignmentId,
                        principalSchema: "fleet",
                        principalTable: "VehicleDriverAssignment",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VehicleGpsPoint_VehicleTrackingDevice_DeviceId",
                        column: x => x.DeviceId,
                        principalSchema: "fleet",
                        principalTable: "VehicleTrackingDevice",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VehicleGpsPoint_Vehicle_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicle",
                        principalColumn: "Id");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_VehicleExpense_Type",
                schema: "fleet",
                table: "VehicleExpense",
                sql: "[Type] IN ('Fuel', 'Maintenance', 'Repair', 'Toll', 'Parking', 'Wash', 'Tires', 'Oil', 'SpareParts', 'Insurance', 'Licensing', 'Other')");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleGpsPoint_AssignmentId",
                schema: "fleet",
                table: "VehicleGpsPoint",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleGpsPoint_DeviceId",
                schema: "fleet",
                table: "VehicleGpsPoint",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleGpsPoint_RecordedAt",
                schema: "fleet",
                table: "VehicleGpsPoint",
                column: "RecordedAt");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleGpsPoint_Vehicle_RecordedAt",
                schema: "fleet",
                table: "VehicleGpsPoint",
                columns: new[] { "VehicleId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_VehicleGpsPoint_User_ClientPoint",
                schema: "fleet",
                table: "VehicleGpsPoint",
                columns: new[] { "UserId", "ClientPointId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_VehicleTrackingAlert_Open",
                schema: "fleet",
                table: "VehicleTrackingAlert",
                columns: new[] { "VehicleId", "RecipientUserId" },
                unique: true,
                filter: "[Status] = 'Open'");

            migrationBuilder.CreateIndex(
                name: "UX_VehicleTrackingDevice_User_Device",
                schema: "fleet",
                table: "VehicleTrackingDevice",
                columns: new[] { "UserId", "DeviceId" },
                unique: true);

            // Written by hand: Access owns access.User (see docs/architecture.md, cross-module keys).
            migrationBuilder.AddForeignKey(
                name: VehicleTrackingDeviceConfiguration.UserForeignKey,
                schema: "fleet",
                table: "VehicleTrackingDevice",
                column: "UserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: VehicleTrackingAlertConfiguration.RecipientForeignKey,
                schema: "fleet",
                table: "VehicleTrackingAlert",
                column: "RecipientUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: VehicleTrackingDeviceConfiguration.UserForeignKey, schema: "fleet", table: "VehicleTrackingDevice");
            migrationBuilder.DropForeignKey(name: VehicleTrackingAlertConfiguration.RecipientForeignKey, schema: "fleet", table: "VehicleTrackingAlert");

            migrationBuilder.DropTable(
                name: "VehicleGpsPoint",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehicleTrackingAlert",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehicleTrackingState",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehicleTrackingDevice",
                schema: "fleet");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VehicleExpense_Type",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropColumn(
                name: "TrackingEnabled",
                schema: "fleet",
                table: "Vehicle");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VehicleExpense_Type",
                schema: "fleet",
                table: "VehicleExpense",
                sql: "[Type] IN ('Fuel', 'Maintenance', 'Repair', 'Toll', 'Parking', 'Wash', 'Other')");
        }
    }
}
