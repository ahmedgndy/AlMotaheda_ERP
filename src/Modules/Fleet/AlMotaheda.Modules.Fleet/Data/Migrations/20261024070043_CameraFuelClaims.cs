using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Fleet.Data.Migrations
{
    /// <inheritdoc />
    internal partial class CameraFuelClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_VehicleExpenseFlag_Kind",
                schema: "fleet",
                table: "VehicleExpenseFlag");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VehicleExpense_Fuel",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FleetAttachment_Purpose",
                schema: "fleet",
                table: "FleetAttachment");

            migrationBuilder.AddColumn<string>(
                name: "AmountSource",
                schema: "fleet",
                table: "VehicleExpense",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CapturedAt",
                schema: "fleet",
                table: "VehicleExpense",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EffectiveUnitPrice",
                schema: "fleet",
                table: "VehicleExpense",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OcrAmount",
                schema: "fleet",
                table: "VehicleExpense",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OcrConfidence",
                schema: "fleet",
                table: "VehicleExpense",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OcrEngine",
                schema: "fleet",
                table: "VehicleExpense",
                type: "varchar(50)",
                unicode: false,
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OcrLiters",
                schema: "fleet",
                table: "VehicleExpense",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OcrText",
                schema: "fleet",
                table: "VehicleExpense",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OcrUnitPrice",
                schema: "fleet",
                table: "VehicleExpense",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FuelPriceItemId",
                schema: "fleet",
                table: "Vehicle",
                type: "int",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_VehicleExpenseFlag_Kind",
                schema: "fleet",
                table: "VehicleExpenseFlag",
                sql: "[Kind] IN ('DuplicateInvoiceNo', 'DuplicateReceipt', 'SameAmountVehicleDay', 'OdometerNotIncreasing', 'ImplausibleDistance', 'PriceAboveRange', 'NoReferencePrice', 'PriceNotValidated', 'AmountManuallyEntered', 'PumpReadingInconsistent', 'LowReadingConfidence', 'PhotoQuality')");

            migrationBuilder.CreateIndex(
                name: "UX_VehicleExpense_OutstandingFuel",
                schema: "fleet",
                table: "VehicleExpense",
                column: "VehicleId",
                unique: true,
                filter: "[Type] = 'Fuel' AND [CapturedAt] IS NOT NULL AND [Status] IN ('Submitted', 'Escalated', 'EvidenceRequested')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VehicleExpense_AmountSource",
                schema: "fleet",
                table: "VehicleExpense",
                sql: "[AmountSource] IS NULL OR [AmountSource] IN ('Ocr', 'ManualCorrection')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VehicleExpense_Fuel",
                schema: "fleet",
                table: "VehicleExpense",
                sql: "([Type] = 'Fuel' AND [CapturedAt] IS NULL AND [FuelQuantity] IS NOT NULL AND [UnitPrice] IS NOT NULL AND [FuelQuantity] > 0 AND [UnitPrice] > 0 AND ABS([Amount] - ROUND([FuelQuantity] * [UnitPrice], 2)) < 0.01) OR ([Type] = 'Fuel' AND [CapturedAt] IS NOT NULL AND [AmountSource] IS NOT NULL AND [UnitPrice] IS NULL AND ([FuelQuantity] IS NULL OR [FuelQuantity] > 0)) OR ([Type] <> 'Fuel' AND [FuelQuantity] IS NULL AND [UnitPrice] IS NULL AND [CapturedAt] IS NULL AND [AmountSource] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VehicleExpense_OcrConfidence",
                schema: "fleet",
                table: "VehicleExpense",
                sql: "[OcrConfidence] IS NULL OR ([OcrConfidence] >= 0 AND [OcrConfidence] <= 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FleetAttachment_Purpose",
                schema: "fleet",
                table: "FleetAttachment",
                sql: "[Purpose] IN ('Odometer', 'Invoice', 'Receipt', 'Violation', 'Supporting', 'PumpDisplay', 'VehicleAtPump')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_VehicleExpenseFlag_Kind",
                schema: "fleet",
                table: "VehicleExpenseFlag");

            migrationBuilder.DropIndex(
                name: "UX_VehicleExpense_OutstandingFuel",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VehicleExpense_AmountSource",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VehicleExpense_Fuel",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VehicleExpense_OcrConfidence",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FleetAttachment_Purpose",
                schema: "fleet",
                table: "FleetAttachment");

            migrationBuilder.DropColumn(
                name: "AmountSource",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropColumn(
                name: "CapturedAt",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropColumn(
                name: "EffectiveUnitPrice",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropColumn(
                name: "OcrAmount",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropColumn(
                name: "OcrConfidence",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropColumn(
                name: "OcrEngine",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropColumn(
                name: "OcrLiters",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropColumn(
                name: "OcrText",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropColumn(
                name: "OcrUnitPrice",
                schema: "fleet",
                table: "VehicleExpense");

            migrationBuilder.DropColumn(
                name: "FuelPriceItemId",
                schema: "fleet",
                table: "Vehicle");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VehicleExpenseFlag_Kind",
                schema: "fleet",
                table: "VehicleExpenseFlag",
                sql: "[Kind] IN ('DuplicateInvoiceNo', 'DuplicateReceipt', 'SameAmountVehicleDay', 'OdometerNotIncreasing', 'ImplausibleDistance', 'PriceAboveRange', 'NoReferencePrice')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VehicleExpense_Fuel",
                schema: "fleet",
                table: "VehicleExpense",
                sql: "([Type] = 'Fuel' AND [FuelQuantity] IS NOT NULL AND [UnitPrice] IS NOT NULL AND [FuelQuantity] > 0 AND [UnitPrice] > 0 AND ABS([Amount] - ROUND([FuelQuantity] * [UnitPrice], 2)) < 0.01) OR ([Type] <> 'Fuel' AND [FuelQuantity] IS NULL AND [UnitPrice] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FleetAttachment_Purpose",
                schema: "fleet",
                table: "FleetAttachment",
                sql: "[Purpose] IN ('Odometer', 'Invoice', 'Receipt', 'Violation', 'Supporting')");
        }
    }
}
