using System;
using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <inheritdoc />
    internal partial class InitialProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "projects");

            migrationBuilder.CreateTable(
                name: "Project",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ClientNameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ClientNameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LocationAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LocationEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContractNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    AddressAr = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AddressEn = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SiteLatitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    SiteLongitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    SiteRadiusMeters = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Project", x => x.Id);
                    table.CheckConstraint("CK_Project_Code", "LEN([Code]) >= 2 AND [Code] COLLATE Latin1_General_BIN NOT LIKE '%[^A-Z0-9-]%'");
                    table.CheckConstraint("CK_Project_Dates", "[EndDate] > [StartDate]");
                    table.CheckConstraint("CK_Project_Site", "([SiteLatitude] IS NULL AND [SiteLongitude] IS NULL) OR ([SiteLatitude] IS NOT NULL AND [SiteLongitude] IS NOT NULL AND [SiteLatitude] BETWEEN -90 AND 90 AND [SiteLongitude] BETWEEN -180 AND 180)");
                    table.CheckConstraint("CK_Project_SiteRadius", "[SiteRadiusMeters] BETWEEN 50 AND 5000");
                    table.CheckConstraint("CK_Project_Status", "[Status] IN ('Planning', 'Active', 'Delayed', 'OnHold', 'Completed')");
                });

            migrationBuilder.CreateTable(
                name: "ProjectMember",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Responsibility = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    AssignedBy = table.Column<int>(type: "int", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    EndedBy = table.Column<int>(type: "int", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectMember", x => x.Id);
                    table.CheckConstraint("CK_ProjectMember_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
                    table.CheckConstraint("CK_ProjectMember_Ending", "([EndDate] IS NULL AND [EndedAt] IS NULL AND [EndedBy] IS NULL AND [EndReason] IS NULL) OR ([EndDate] IS NOT NULL AND [EndedAt] IS NOT NULL AND [EndedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_ProjectMember_Responsibility", "[Responsibility] IN ('ProjectManager', 'SiteEngineer', 'TeamMember')");
                    table.ForeignKey(
                        name: "FK_ProjectMember_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Project_Status",
                schema: "projects",
                table: "Project",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "UX_Project_Code",
                schema: "projects",
                table: "Project",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMember_ProjectId",
                schema: "projects",
                table: "ProjectMember",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMember_UserId_Active",
                schema: "projects",
                table: "ProjectMember",
                column: "UserId",
                filter: "[EndDate] IS NULL")
                .Annotation("SqlServer:Include", new[] { "ProjectId", "Responsibility" });

            migrationBuilder.CreateIndex(
                name: "UX_ProjectMember_ActiveManager",
                schema: "projects",
                table: "ProjectMember",
                column: "ProjectId",
                unique: true,
                filter: "[Responsibility] = 'ProjectManager' AND [EndDate] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ProjectMember_ActiveUser",
                schema: "projects",
                table: "ProjectMember",
                columns: new[] { "ProjectId", "UserId" },
                unique: true,
                filter: "[EndDate] IS NULL");

            // Written by hand: access.User belongs to the Access module, so it isn't in this module's model.
            // Users are never deleted, so the key never blocks; it stops a membership for a user that doesn't exist.
            // The Access migrations must be applied before this one.
            migrationBuilder.AddForeignKey(
                name: ProjectMemberConfiguration.UserForeignKey,
                schema: "projects",
                table: "ProjectMember",
                column: "UserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: ProjectMemberConfiguration.UserForeignKey,
                schema: "projects",
                table: "ProjectMember");

            migrationBuilder.DropTable(
                name: "ProjectMember",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "Project",
                schema: "projects");
        }
    }
}
