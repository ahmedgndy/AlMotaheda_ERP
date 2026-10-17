using System;
using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <inheritdoc />
    internal partial class SiteLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LocationAlert",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    EngineerUserId = table.Column<int>(type: "int", nullable: false),
                    RecipientUserId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationAlert", x => x.Id);
                    table.CheckConstraint("CK_LocationAlert_Ended", "([Status] = 'Open' AND [EndedAt] IS NULL) OR ([Status] <> 'Open' AND [EndedAt] IS NOT NULL)");
                    table.CheckConstraint("CK_LocationAlert_Kind", "[Kind] IN ('Unavailable', 'OutsideSite')");
                    table.CheckConstraint("CK_LocationAlert_Parties", "[EngineerUserId] <> [RecipientUserId]");
                    table.CheckConstraint("CK_LocationAlert_Status", "[Status] IN ('Open', 'Recovered', 'Ended')");
                    table.ForeignKey(
                        name: "FK_LocationAlert_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "LocationPing",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    AccuracyMeters = table.Column<int>(type: "int", nullable: true),
                    DistanceMeters = table.Column<int>(type: "int", nullable: true),
                    InsideSite = table.Column<bool>(type: "bit", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationPing", x => x.Id);
                    table.CheckConstraint("CK_LocationPing_Position", "[Latitude] BETWEEN -90 AND 90 AND [Longitude] BETWEEN -180 AND 180 AND ([AccuracyMeters] IS NULL OR [AccuracyMeters] >= 0)");
                    table.ForeignKey(
                        name: "FK_LocationPing_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "LocationSharing",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    IsOn = table.Column<bool>(type: "bit", nullable: false),
                    TurnedOnAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    TurnedOffAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastPingAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastLatitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    LastLongitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    LastAccuracyMeters = table.Column<int>(type: "int", nullable: true),
                    LastDistanceMeters = table.Column<int>(type: "int", nullable: true),
                    UnavailableSince = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    OutsideSince = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    OutsidePings = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationSharing", x => x.Id);
                    table.CheckConstraint("CK_LocationSharing_On", "[IsOn] = 0 OR [TurnedOnAt] IS NOT NULL");
                    table.CheckConstraint("CK_LocationSharing_Position", "([LastLatitude] IS NULL AND [LastLongitude] IS NULL) OR ([LastLatitude] BETWEEN -90 AND 90 AND [LastLongitude] BETWEEN -180 AND 180)");
                    table.ForeignKey(
                        name: "FK_LocationSharing_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocationAlert_Project_StartedAt",
                schema: "projects",
                table: "LocationAlert",
                columns: new[] { "ProjectId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_LocationAlert_Open",
                schema: "projects",
                table: "LocationAlert",
                columns: new[] { "ProjectId", "EngineerUserId", "Kind", "RecipientUserId" },
                unique: true,
                filter: "[Status] = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_LocationPing_Project_User_RecordedAt",
                schema: "projects",
                table: "LocationPing",
                columns: new[] { "ProjectId", "UserId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LocationPing_ReceivedAt",
                schema: "projects",
                table: "LocationPing",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_LocationSharing_On",
                schema: "projects",
                table: "LocationSharing",
                column: "IsOn",
                filter: "[IsOn] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_LocationSharing_Project_User",
                schema: "projects",
                table: "LocationSharing",
                columns: new[] { "ProjectId", "UserId" },
                unique: true);

            // The users are Access records: these keys are added by hand (Access owns the table). Positions have
            // none: they are only written for the owner of a sharing row, which has one.
            migrationBuilder.AddForeignKey(
                name: LocationSharingConfiguration.UserForeignKey,
                schema: "projects",
                table: "LocationSharing",
                column: "UserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
            migrationBuilder.AddForeignKey(
                name: LocationAlertConfiguration.EngineerForeignKey,
                schema: "projects",
                table: "LocationAlert",
                column: "EngineerUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
            migrationBuilder.AddForeignKey(
                name: LocationAlertConfiguration.RecipientForeignKey,
                schema: "projects",
                table: "LocationAlert",
                column: "RecipientUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocationAlert",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "LocationPing",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "LocationSharing",
                schema: "projects");
        }
    }
}
