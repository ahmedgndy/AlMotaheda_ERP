using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Fleet.Data.Migrations
{
    /// <inheritdoc />
    internal partial class ClaimOdometerOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_VehicleExpense_Odometer",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.AlterColumn<decimal>(
                name: "OdometerKm",
                schema: "fleet",
                table: "VehicleExpense",
                type: "decimal(18,1)",
                precision: 18,
                scale: 1,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,1)",
                oldPrecision: 18,
                oldScale: 1);

            migrationBuilder.AddCheckConstraint(
                name: "CK_VehicleExpense_Odometer",
                schema: "fleet",
                table: "VehicleExpense",
                sql: "[OdometerKm] IS NULL OR [OdometerKm] >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_VehicleExpense_Odometer",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.AlterColumn<decimal>(
                name: "OdometerKm",
                schema: "fleet",
                table: "VehicleExpense",
                type: "decimal(18,1)",
                precision: 18,
                scale: 1,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,1)",
                oldPrecision: 18,
                oldScale: 1,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_VehicleExpense_Odometer",
                schema: "fleet",
                table: "VehicleExpense",
                sql: "[OdometerKm] >= 0");
        }
    }
}
