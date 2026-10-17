using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AlMotaheda.Modules.Access.Data.Migrations
{
    /// <inheritdoc />
    internal partial class AddFinancialManagerRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Accountant asks for price changes (catalog Edit). An Executive may already have granted it on the
            // Roles page, so the row is added only when it is missing.
            migrationBuilder.Sql(
                """
                IF NOT EXISTS (SELECT 1 FROM [access].[RolePermission] WHERE [RoleId] = 5 AND [PermissionId] = 53)
                    INSERT INTO [access].[RolePermission] ([PermissionId], [RoleId], [CreatedAt], [CreatedBy])
                    VALUES (53, 5, '2026-10-11T00:00:00', NULL);
                """);

            migrationBuilder.InsertData(
                schema: "access",
                table: "Role",
                columns: new[] { "Id", "Code", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy" },
                values: new object[] { 9, "financial_manager", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null });

            migrationBuilder.InsertData(
                schema: "access",
                table: "RolePermission",
                columns: new[] { "PermissionId", "RoleId", "CreatedAt", "CreatedBy" },
                values: new object[,]
                {
                    { 1, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 5, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 6, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 10, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 11, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 15, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 16, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 20, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 26, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 30, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 31, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 35, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 51, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 54, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 55, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 56, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 57, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 66, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 70, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 71, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 75, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 91, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 95, 9, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 53, 5 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 1, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 5, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 6, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 10, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 11, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 15, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 16, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 20, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 26, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 30, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 31, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 35, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 51, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 54, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 55, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 56, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 57, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 66, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 70, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 71, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 75, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 91, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 95, 9 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "Role",
                keyColumn: "Id",
                keyValue: 9);
        }
    }
}
