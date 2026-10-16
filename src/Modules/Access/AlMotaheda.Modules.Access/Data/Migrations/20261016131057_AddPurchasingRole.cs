using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AlMotaheda.Modules.Access.Data.Migrations
{
    /// <inheritdoc />
    internal partial class AddPurchasingRole : Migration
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
                    { 91, "View", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "procurement" },
                    { 92, "Create", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "procurement" },
                    { 93, "Edit", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "procurement" },
                    { 94, "Approve", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "procurement" },
                    { 95, "SeeMoney", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, "procurement" }
                });

            migrationBuilder.InsertData(
                schema: "access",
                table: "Role",
                columns: new[] { "Id", "Code", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy" },
                values: new object[] { 8, "purchasing", new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null });

            migrationBuilder.InsertData(
                schema: "access",
                table: "RolePermission",
                columns: new[] { "PermissionId", "RoleId", "CreatedAt", "CreatedBy" },
                values: new object[,]
                {
                    { 91, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 92, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 93, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 94, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 95, 1, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 91, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 92, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 94, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 95, 2, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 91, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 92, 3, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 91, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 94, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 95, 5, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 91, 8, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 92, 8, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 93, 8, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 95, 8, new DateTime(2026, 10, 11, 0, 0, 0, 0, DateTimeKind.Utc), null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 91, 1 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 92, 1 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 93, 1 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 94, 1 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 95, 1 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 91, 2 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 92, 2 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 94, 2 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 95, 2 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 91, 3 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 92, 3 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 91, 5 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 94, 5 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 95, 5 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 91, 8 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 92, 8 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 93, 8 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "RolePermission",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { 95, 8 });

            migrationBuilder.DeleteData(
                schema: "access",
                table: "Permission",
                keyColumn: "Id",
                keyValue: 91);

            migrationBuilder.DeleteData(
                schema: "access",
                table: "Permission",
                keyColumn: "Id",
                keyValue: 92);

            migrationBuilder.DeleteData(
                schema: "access",
                table: "Permission",
                keyColumn: "Id",
                keyValue: 93);

            migrationBuilder.DeleteData(
                schema: "access",
                table: "Permission",
                keyColumn: "Id",
                keyValue: 94);

            migrationBuilder.DeleteData(
                schema: "access",
                table: "Permission",
                keyColumn: "Id",
                keyValue: 95);

            migrationBuilder.DeleteData(
                schema: "access",
                table: "Role",
                keyColumn: "Id",
                keyValue: 8);
        }
    }
}
