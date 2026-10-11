using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AlMotaheda.Modules.Access.Data.Migrations
{
    /// <inheritdoc />
    internal partial class InitialAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "access");

            migrationBuilder.CreateTable(
                name: "Permission",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Module = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Action = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permission", x => x.Id);
                    table.CheckConstraint("CK_Permission_Action", "[Action] IN ('View', 'Create', 'Edit', 'Approve', 'SeeMoney')");
                });

            migrationBuilder.CreateTable(
                name: "RefreshToken",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsPersistent = table.Column<bool>(type: "bit", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RevokedReason = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshToken", x => x.Id);
                    table.CheckConstraint("CK_RefreshToken_Revocation", "([RevokedAt] IS NULL AND [RevokedReason] IS NULL) OR ([RevokedAt] IS NOT NULL AND [RevokedReason] IS NOT NULL)");
                    table.CheckConstraint("CK_RefreshToken_RevokedReason", "[RevokedReason] IN ('Rotated', 'SignedOut', 'ReuseDetected', 'PasswordChanged', 'PasswordReset', 'UserDisabled')");
                });

            migrationBuilder.CreateTable(
                name: "Role",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Role", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "User",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Phone = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    NameAr = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    JobTitleAr = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    JobTitleEn = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false, defaultValue: "Active"),
                    PasswordHash = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User", x => x.Id);
                    table.CheckConstraint("CK_User_Phone", "[Phone] NOT LIKE '%[^0-9+]%'");
                    table.CheckConstraint("CK_User_Status", "[Status] IN ('Active', 'Disabled')");
                    table.ForeignKey(
                        name: "FK_User_Role_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "access",
                        principalTable: "Role",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_User_User_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "access",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_User_User_UpdatedBy",
                        column: x => x.UpdatedBy,
                        principalSchema: "access",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RolePermission",
                schema: "access",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    PermissionId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermission", x => new { x.RoleId, x.PermissionId });
                    table.ForeignKey(
                        name: "FK_RolePermission_Permission_PermissionId",
                        column: x => x.PermissionId,
                        principalSchema: "access",
                        principalTable: "Permission",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RolePermission_Role_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "access",
                        principalTable: "Role",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RolePermission_User_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "access",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "UserPermissionOverride",
                schema: "access",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    PermissionId = table.Column<int>(type: "int", nullable: false),
                    IsGranted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPermissionOverride", x => new { x.UserId, x.PermissionId });
                    table.ForeignKey(
                        name: "FK_UserPermissionOverride_Permission_PermissionId",
                        column: x => x.PermissionId,
                        principalSchema: "access",
                        principalTable: "Permission",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserPermissionOverride_User_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "access",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserPermissionOverride_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "access",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "UserSignInState",
                schema: "access",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    FailedLoginCount = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    LockoutEndsAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSignInState", x => x.UserId);
                    table.CheckConstraint("CK_UserSignInState_FailedLoginCount", "[FailedLoginCount] >= 0");
                    table.ForeignKey(
                        name: "FK_UserSignInState_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "access",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.InsertData(
                schema: "access",
                table: "Permission",
                columns: new[] { "Id", "Action", "CreatedAt", "CreatedBy", "Module" },
                values: new object[,]
                {
                    { 1, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "projects" },
                    { 2, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "projects" },
                    { 3, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "projects" },
                    { 4, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "projects" },
                    { 5, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "projects" },
                    { 6, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "subcontractors" },
                    { 7, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "subcontractors" },
                    { 8, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "subcontractors" },
                    { 9, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "subcontractors" },
                    { 10, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "subcontractors" },
                    { 11, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "workers" },
                    { 12, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "workers" },
                    { 13, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "workers" },
                    { 14, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "workers" },
                    { 15, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "workers" },
                    { 16, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "site" },
                    { 17, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "site" },
                    { 18, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "site" },
                    { 19, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "site" },
                    { 20, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "site" },
                    { 21, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "fuel" },
                    { 22, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "fuel" },
                    { 23, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "fuel" },
                    { 24, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "fuel" },
                    { 25, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "fuel" },
                    { 26, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "fleet" },
                    { 27, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "fleet" },
                    { 28, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "fleet" },
                    { 29, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "fleet" },
                    { 30, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "fleet" },
                    { 31, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "equipment" },
                    { 32, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "equipment" },
                    { 33, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "equipment" },
                    { 34, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "equipment" },
                    { 35, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "equipment" },
                    { 36, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "office" },
                    { 37, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "office" },
                    { 38, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "office" },
                    { 39, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "office" },
                    { 40, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "office" },
                    { 41, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "custody" },
                    { 42, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "custody" },
                    { 43, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "custody" },
                    { 44, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "custody" },
                    { 45, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "custody" },
                    { 46, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "income" },
                    { 47, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "income" },
                    { 48, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "income" },
                    { 49, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "income" },
                    { 50, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "income" },
                    { 51, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "catalog" },
                    { 52, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "catalog" },
                    { 53, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "catalog" },
                    { 54, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "catalog" },
                    { 55, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "catalog" },
                    { 56, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "reports" },
                    { 57, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "reports" },
                    { 58, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "reports" },
                    { 59, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "reports" },
                    { 60, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "reports" },
                    { 61, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "monitoring" },
                    { 62, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "monitoring" },
                    { 63, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "monitoring" },
                    { 64, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "monitoring" },
                    { 65, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "monitoring" },
                    { 66, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "payments" },
                    { 67, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "payments" },
                    { 68, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "payments" },
                    { 69, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "payments" },
                    { 70, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "payments" },
                    { 71, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "finance" },
                    { 72, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "finance" },
                    { 73, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "finance" },
                    { 74, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "finance" },
                    { 75, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "finance" },
                    { 76, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "accounts" },
                    { 77, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "accounts" },
                    { 78, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "accounts" },
                    { 79, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "accounts" },
                    { 80, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "accounts" },
                    { 81, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "settings" },
                    { 82, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "settings" },
                    { 83, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "settings" },
                    { 84, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "settings" },
                    { 85, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "settings" }
                });

            migrationBuilder.InsertData(
                schema: "access",
                table: "Role",
                columns: new[] { "Id", "Code", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy" },
                values: new object[,]
                {
                    { 1, "executive", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null },
                    { 2, "manager", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null },
                    { 3, "engineer", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null },
                    { 4, "office_admin", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null },
                    { 5, "accountant", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null },
                    { 6, "staff", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null }
                });

            migrationBuilder.InsertData(
                schema: "access",
                table: "RolePermission",
                columns: new[] { "PermissionId", "RoleId", "CreatedAt", "CreatedBy" },
                values: new object[,]
                {
                    { 1, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 2, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 3, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 4, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 5, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 6, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 7, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 8, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 9, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 10, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 11, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 12, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 13, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 14, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 15, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 16, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 17, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 18, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 19, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 20, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 21, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 22, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 23, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 24, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 25, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 26, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 27, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 28, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 29, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 30, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 31, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 32, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 33, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 34, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 35, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 36, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 37, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 38, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 39, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 40, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 41, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 42, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 43, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 44, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 45, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 46, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 47, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 48, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 49, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 50, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 51, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 52, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 53, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 54, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 55, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 56, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 57, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 58, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 59, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 60, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 61, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 63, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 66, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 67, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 68, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 69, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 70, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 71, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 72, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 73, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 74, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 75, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 76, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 77, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 78, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 79, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 80, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 81, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 82, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 83, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 84, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 85, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 1, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 2, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 3, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 5, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 6, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 7, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 8, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 10, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 11, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 12, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 14, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 15, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 16, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 17, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 19, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 20, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 21, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 24, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 25, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 26, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 27, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 30, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 31, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 33, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 34, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 41, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 46, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 51, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 52, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 53, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 55, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 56, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 57, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 58, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 59, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 61, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 66, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 1, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 2, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 11, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 12, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 16, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 17, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 21, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 22, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 26, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 27, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 31, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 41, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 42, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 51, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 56, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 57, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 36, 4, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 37, 4, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 38, 4, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 41, 4, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 42, 4, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 56, 4, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 57, 4, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 66, 4, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 1, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 4, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 5, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 6, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 7, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 8, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 10, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 11, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 12, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 13, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 14, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 15, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 16, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 20, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 21, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 24, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 25, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 26, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 29, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 30, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 31, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 34, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 35, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 36, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 37, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 38, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 39, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 40, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 41, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 44, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 45, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 46, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 47, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 48, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 50, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 51, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 55, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 56, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 57, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 58, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 59, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 66, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 67, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 70, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 71, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 72, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 73, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 74, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 75, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 76, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 1, 6, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 21, 6, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 22, 6, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 26, 6, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 31, 6, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 41, 6, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 42, 6, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 56, 6, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 57, 6, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Permission_CreatedBy",
                schema: "access",
                table: "Permission",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "UX_Permission_Module_Action",
                schema: "access",
                table: "Permission",
                columns: new[] { "Module", "Action" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshToken_ExpiresAt",
                schema: "access",
                table: "RefreshToken",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshToken_FamilyId",
                schema: "access",
                table: "RefreshToken",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshToken_UserId_Active",
                schema: "access",
                table: "RefreshToken",
                column: "UserId",
                filter: "[RevokedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_RefreshToken_TokenHash",
                schema: "access",
                table: "RefreshToken",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Role_CreatedBy",
                schema: "access",
                table: "Role",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Role_UpdatedBy",
                schema: "access",
                table: "Role",
                column: "UpdatedBy");

            migrationBuilder.CreateIndex(
                name: "UX_Role_Code",
                schema: "access",
                table: "Role",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RolePermission_CreatedBy",
                schema: "access",
                table: "RolePermission",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermission_PermissionId",
                schema: "access",
                table: "RolePermission",
                column: "PermissionId");

            migrationBuilder.CreateIndex(
                name: "IX_User_CreatedBy",
                schema: "access",
                table: "User",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_User_RoleId",
                schema: "access",
                table: "User",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_User_UpdatedBy",
                schema: "access",
                table: "User",
                column: "UpdatedBy");

            migrationBuilder.CreateIndex(
                name: "UX_User_NormalizedEmail",
                schema: "access",
                table: "User",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_User_Phone",
                schema: "access",
                table: "User",
                column: "Phone",
                unique: true,
                filter: "[Phone] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserPermissionOverride_CreatedBy",
                schema: "access",
                table: "UserPermissionOverride",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_UserPermissionOverride_PermissionId",
                schema: "access",
                table: "UserPermissionOverride",
                column: "PermissionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Permission_User_CreatedBy",
                schema: "access",
                table: "Permission",
                column: "CreatedBy",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_RefreshToken_User_UserId",
                schema: "access",
                table: "RefreshToken",
                column: "UserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Role_User_CreatedBy",
                schema: "access",
                table: "Role",
                column: "CreatedBy",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Role_User_UpdatedBy",
                schema: "access",
                table: "Role",
                column: "UpdatedBy",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Role_User_CreatedBy",
                schema: "access",
                table: "Role");

            migrationBuilder.DropForeignKey(
                name: "FK_Role_User_UpdatedBy",
                schema: "access",
                table: "Role");

            migrationBuilder.DropTable(
                name: "RefreshToken",
                schema: "access");

            migrationBuilder.DropTable(
                name: "RolePermission",
                schema: "access");

            migrationBuilder.DropTable(
                name: "UserPermissionOverride",
                schema: "access");

            migrationBuilder.DropTable(
                name: "UserSignInState",
                schema: "access");

            migrationBuilder.DropTable(
                name: "Permission",
                schema: "access");

            migrationBuilder.DropTable(
                name: "User",
                schema: "access");

            migrationBuilder.DropTable(
                name: "Role",
                schema: "access");
        }
    }
}
