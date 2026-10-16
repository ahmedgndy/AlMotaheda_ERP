using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AlMotaheda.Modules.Access.Data.Migrations
{
    /// <inheritdoc />
    internal partial class AddHrRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "access",
                table: "Permission",
                columns: new[] { "Id", "Action", "CreatedAt", "CreatedBy", "Module" },
                values: new object[,]
                {
                    { 86, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "hr" },
                    { 87, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "hr" },
                    { 88, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "hr" },
                    { 89, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "hr" },
                    { 90, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "hr" }
                });

            migrationBuilder.InsertData(
                schema: "access",
                table: "Role",
                columns: new[] { "Id", "Code", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy" },
                values: new object[] { 7, "hr", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null });

            migrationBuilder.InsertData(
                schema: "access",
                table: "RolePermission",
                columns: new[] { "PermissionId", "RoleId", "CreatedAt", "CreatedBy" },
                values: new object[,]
                {
                    { 86, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 87, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 88, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 89, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 90, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 86, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 89, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 90, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 86, 7, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 87, 7, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 88, 7, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 90, 7, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 86, 1 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 87, 1 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 88, 1 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 89, 1 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 90, 1 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 86, 5 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 89, 5 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 90, 5 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 86, 7 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 87, 7 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 88, 7 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 90, 7 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "Permission",
                keyColumn: "Id",
                keyValue: 86);

            migrationBuilder.DeleteData(
                schema: "access",
                table: "Permission",
                keyColumn: "Id",
                keyValue: 87);

            migrationBuilder.DeleteData(
                schema: "access",
                table: "Permission",
                keyColumn: "Id",
                keyValue: 88);

            migrationBuilder.DeleteData(
                schema: "access",
                table: "Permission",
                keyColumn: "Id",
                keyValue: 89);

            migrationBuilder.DeleteData(
                schema: "access",
                table: "Permission",
                keyColumn: "Id",
                keyValue: 90);

            migrationBuilder.DeleteData(
                schema: "access",
                table: "Role",
                keyColumn: "Id",
                keyValue: 7);
        }
    }
}
