using System;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Fleet.Data.Migrations
{
    /// <summary>
    /// Vehicles (docs/project-workspace/12-treasury-custody.md): the register extension of an equipment row of the new
    /// category Vehicle (plate, odometer, GPS status), drivers over time, the vehicle's append-only operating custody,
    /// expense claims with their review flags and alerts, evidence attachments and violations. Only adds; no row is
    /// changed. The equipment category check is widened with <c>Vehicle</c>.
    /// </summary>
    /// <remarks>
    /// The driver columns point to <c>hr.Employee</c>, so the HR migrations run before Fleet's. Down drops the new
    /// tables and narrows the category check again; it fails if a vehicle row exists (remove the vehicles first).
    /// </remarks>
    internal partial class VehiclesAndCustody : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Equipment_Category",
                schema: "fleet",
                table: "Equipment");

            migrationBuilder.CreateTable(
                name: "FleetAttachment",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Purpose = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    OwnerType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    OwnerId = table.Column<int>(type: "int", nullable: true),
                    StorageKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UploadedBy = table.Column<int>(type: "int", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    BoundAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetAttachment", x => x.Id);
                    table.CheckConstraint("CK_FleetAttachment_Owner", "([OwnerType] IS NULL AND [OwnerId] IS NULL) OR ([OwnerType] IN ('VehicleExpense', 'VehicleViolation') AND [OwnerId] IS NOT NULL)");
                    table.CheckConstraint("CK_FleetAttachment_Purpose", "[Purpose] IN ('Odometer', 'Invoice', 'Receipt', 'Violation', 'Supporting')");
                    table.CheckConstraint("CK_FleetAttachment_Sha256", "LEN([Sha256]) = 64 AND [Sha256] COLLATE Latin1_General_BIN NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_FleetAttachment_Size", "[SizeBytes] > 0");
                });

            migrationBuilder.CreateTable(
                name: "Vehicle",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Plate = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Type = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ModelYear = table.Column<int>(type: "int", nullable: true),
                    FuelType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    OdometerKm = table.Column<decimal>(type: "decimal(18,1)", precision: 18, scale: 1, nullable: false),
                    OdometerUpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    OdometerUpdatedBy = table.Column<int>(type: "int", nullable: true),
                    GpsProvider = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    GpsDeviceId = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    GpsStatus = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicle", x => x.Id);
                    table.CheckConstraint("CK_Vehicle_FuelType", "[FuelType] IN ('Petrol', 'Diesel', 'Gas', 'Electric')");
                    table.CheckConstraint("CK_Vehicle_Gps", "[GpsStatus] = 'NotConfigured' OR ([GpsProvider] IS NOT NULL AND [GpsDeviceId] IS NOT NULL)");
                    table.CheckConstraint("CK_Vehicle_GpsStatus", "[GpsStatus] IN ('NotConfigured', 'Configured', 'Unavailable')");
                    table.CheckConstraint("CK_Vehicle_ModelYear", "[ModelYear] IS NULL OR ([ModelYear] >= 1950 AND [ModelYear] <= 2100)");
                    table.CheckConstraint("CK_Vehicle_Odometer", "[OdometerKm] >= 0");
                    table.CheckConstraint("CK_Vehicle_Type", "[Type] IN ('Car', 'Pickup', 'Van', 'Truck', 'Bus', 'Motorcycle', 'Other')");
                    table.ForeignKey(
                        name: "FK_Vehicle_Equipment_Id",
                        column: x => x.Id,
                        principalSchema: "fleet",
                        principalTable: "Equipment",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VehicleCustodyEntry",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VehicleId = table.Column<int>(type: "int", nullable: false),
                    DriverEmployeeId = table.Column<int>(type: "int", nullable: true),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    EntryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    SourceType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    SourceId = table.Column<int>(type: "int", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleCustodyEntry", x => x.Id);
                    table.CheckConstraint("CK_VehicleCustodyEntry_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_VehicleCustodyEntry_Kind", "[Kind] IN ('Funding', 'Return', 'Expense', 'AdjustmentIn', 'AdjustmentOut')");
                    table.CheckConstraint("CK_VehicleCustodyEntry_Pending", "[Status] = 'Posted' OR [Kind] = 'Expense'");
                    table.CheckConstraint("CK_VehicleCustodyEntry_SourceType", "[SourceType] IN ('CustodyTransfer', 'CustodyTransferReversal', 'VehicleExpense', 'CustodyAdjustment')");
                    table.CheckConstraint("CK_VehicleCustodyEntry_Status", "[Status] IN ('Pending', 'Posted', 'Released')");
                    table.ForeignKey(
                        name: "FK_VehicleCustodyEntry_Vehicle_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicle",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VehicleDriverAssignment",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VehicleId = table.Column<int>(type: "int", nullable: false),
                    DriverEmployeeId = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    StartOdometerKm = table.Column<decimal>(type: "decimal(18,1)", precision: 18, scale: 1, nullable: true),
                    EndOdometerKm = table.Column<decimal>(type: "decimal(18,1)", precision: 18, scale: 1, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HandoverNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HandoverCustodyBalance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    HandoverPendingClaims = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    AssignedBy = table.Column<int>(type: "int", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    EndedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleDriverAssignment", x => x.Id);
                    table.CheckConstraint("CK_VehicleDriverAssignment_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
                    table.CheckConstraint("CK_VehicleDriverAssignment_End", "([EndDate] IS NULL AND [EndedAt] IS NULL AND [EndedBy] IS NULL) OR ([EndDate] IS NOT NULL AND [EndedAt] IS NOT NULL AND [EndedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_VehicleDriverAssignment_Odometer", "([StartOdometerKm] IS NULL OR [StartOdometerKm] >= 0) AND ([EndOdometerKm] IS NULL OR [EndOdometerKm] >= 0)");
                    table.ForeignKey(
                        name: "FK_VehicleDriverAssignment_Vehicle_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicle",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VehicleExpense",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('VX-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    VehicleId = table.Column<int>(type: "int", nullable: false),
                    DriverEmployeeId = table.Column<int>(type: "int", nullable: false),
                    DriverAssignmentId = table.Column<int>(type: "int", nullable: true),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    Type = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ExpenseDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OdometerKm = table.Column<decimal>(type: "decimal(18,1)", precision: 18, scale: 1, nullable: false),
                    FuelQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    VendorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SupplierId = table.Column<int>(type: "int", nullable: true),
                    InvoiceNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PriceItemId = table.Column<int>(type: "int", nullable: true),
                    PriceCheck = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    ReferenceMaxPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    PriceJustification = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PriceExceptionId = table.Column<int>(type: "int", nullable: true),
                    GpsLatitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    GpsLongitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    GpsCapturedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Round = table.Column<int>(type: "int", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PostingStatus = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    CustodyEntryId = table.Column<int>(type: "int", nullable: true),
                    PostedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReversedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReversedBy = table.Column<int>(type: "int", nullable: true),
                    ReversalReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HrReferralId = table.Column<int>(type: "int", nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleExpense", x => x.Id);
                    table.CheckConstraint("CK_VehicleExpense_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_VehicleExpense_Approved", "[Status] <> 'Approved' OR [PostingStatus] <> 'NotPosted'");
                    table.CheckConstraint("CK_VehicleExpense_Fuel", "([Type] = 'Fuel' AND [FuelQuantity] IS NOT NULL AND [UnitPrice] IS NOT NULL AND [FuelQuantity] > 0 AND [UnitPrice] > 0 AND ABS([Amount] - ROUND([FuelQuantity] * [UnitPrice], 2)) < 0.01) OR ([Type] <> 'Fuel' AND [FuelQuantity] IS NULL AND [UnitPrice] IS NULL)");
                    table.CheckConstraint("CK_VehicleExpense_Gps", "([GpsLatitude] IS NULL AND [GpsLongitude] IS NULL) OR ([GpsLatitude] IS NOT NULL AND [GpsLongitude] IS NOT NULL)");
                    table.CheckConstraint("CK_VehicleExpense_Odometer", "[OdometerKm] >= 0");
                    table.CheckConstraint("CK_VehicleExpense_Posting", "([PostingStatus] = 'NotPosted' AND [CustodyEntryId] IS NULL AND [PostedAt] IS NULL) OR ([PostingStatus] IN ('Posted', 'Reversed') AND [Status] = 'Approved' AND [CustodyEntryId] IS NOT NULL AND [PostedAt] IS NOT NULL)");
                    table.CheckConstraint("CK_VehicleExpense_PostingStatus", "[PostingStatus] IN ('NotPosted', 'Posted', 'Reversed')");
                    table.CheckConstraint("CK_VehicleExpense_Referral", "[HrReferralId] IS NULL OR [Status] = 'Rejected'");
                    table.CheckConstraint("CK_VehicleExpense_Reversal", "([PostingStatus] <> 'Reversed' AND [ReversedAt] IS NULL AND [ReversedBy] IS NULL AND [ReversalReason] IS NULL) OR ([PostingStatus] = 'Reversed' AND [ReversedAt] IS NOT NULL AND [ReversedBy] IS NOT NULL AND [ReversalReason] IS NOT NULL)");
                    table.CheckConstraint("CK_VehicleExpense_Round", "[Round] >= 1");
                    table.CheckConstraint("CK_VehicleExpense_Status", "[Status] IN ('Submitted', 'Approved', 'Rejected', 'EvidenceRequested', 'Escalated')");
                    table.CheckConstraint("CK_VehicleExpense_Type", "[Type] IN ('Fuel', 'Maintenance', 'Repair', 'Toll', 'Parking', 'Wash', 'Other')");
                    table.ForeignKey(
                        name: "FK_VehicleExpense_VehicleCustodyEntry_CustodyEntryId",
                        column: x => x.CustodyEntryId,
                        principalSchema: "fleet",
                        principalTable: "VehicleCustodyEntry",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VehicleExpense_VehicleDriverAssignment_DriverAssignmentId",
                        column: x => x.DriverAssignmentId,
                        principalSchema: "fleet",
                        principalTable: "VehicleDriverAssignment",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VehicleExpense_Vehicle_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicle",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VehicleViolation",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('VV-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    VehicleId = table.Column<int>(type: "int", nullable: false),
                    DriverEmployeeId = table.Column<int>(type: "int", nullable: true),
                    DriverAssignmentId = table.Column<int>(type: "int", nullable: true),
                    ViolationDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Category = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    TicketNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TripReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    GpsLatitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    GpsLongitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Resolution = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HrReferralId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleViolation", x => x.Id);
                    table.CheckConstraint("CK_VehicleViolation_Amount", "[Amount] IS NULL OR [Amount] > 0");
                    table.CheckConstraint("CK_VehicleViolation_Category", "[Category] IN ('Speeding', 'Parking', 'Traffic', 'Accident', 'Damage', 'Misuse', 'Other')");
                    table.CheckConstraint("CK_VehicleViolation_Gps", "([GpsLatitude] IS NULL AND [GpsLongitude] IS NULL) OR ([GpsLatitude] IS NOT NULL AND [GpsLongitude] IS NOT NULL)");
                    table.CheckConstraint("CK_VehicleViolation_Review", "([Status] IN ('Recorded', 'UnderReview') AND [Resolution] IS NULL) OR ([Status] IN ('Resolved', 'Dismissed') AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_VehicleViolation_Status", "[Status] IN ('Recorded', 'UnderReview', 'Resolved', 'Dismissed')");
                    table.ForeignKey(
                        name: "FK_VehicleViolation_VehicleDriverAssignment_DriverAssignmentId",
                        column: x => x.DriverAssignmentId,
                        principalSchema: "fleet",
                        principalTable: "VehicleDriverAssignment",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VehicleViolation_Vehicle_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicle",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VehicleExpenseAlert",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExpenseId = table.Column<int>(type: "int", nullable: false),
                    RecipientUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleExpenseAlert", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleExpenseAlert_VehicleExpense_ExpenseId",
                        column: x => x.ExpenseId,
                        principalSchema: "fleet",
                        principalTable: "VehicleExpense",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VehicleExpenseFlag",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExpenseId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    RelatedExpenseId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleExpenseFlag", x => x.Id);
                    table.CheckConstraint("CK_VehicleExpenseFlag_Kind", "[Kind] IN ('DuplicateInvoiceNo', 'DuplicateReceipt', 'SameAmountVehicleDay', 'OdometerNotIncreasing', 'ImplausibleDistance', 'PriceAboveRange', 'NoReferencePrice')");
                    table.ForeignKey(
                        name: "FK_VehicleExpenseFlag_VehicleExpense_ExpenseId",
                        column: x => x.ExpenseId,
                        principalSchema: "fleet",
                        principalTable: "VehicleExpense",
                        principalColumn: "Id");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Equipment_Category",
                schema: "fleet",
                table: "Equipment",
                sql: "[Category] IN ('Tools', 'Medium', 'Heavy', 'Survey', 'Safety', 'Vehicle')");

            migrationBuilder.CreateIndex(
                name: "IX_FleetAttachment_Owner",
                schema: "fleet",
                table: "FleetAttachment",
                columns: new[] { "OwnerType", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_FleetAttachment_Sha256",
                schema: "fleet",
                table: "FleetAttachment",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "UX_FleetAttachment_IdempotencyKey",
                schema: "fleet",
                table: "FleetAttachment",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_FleetAttachment_StorageKey",
                schema: "fleet",
                table: "FleetAttachment",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Vehicle_Plate",
                schema: "fleet",
                table: "Vehicle",
                column: "Plate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleCustodyEntry_DriverEmployeeId",
                schema: "fleet",
                table: "VehicleCustodyEntry",
                column: "DriverEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleCustodyEntry_Vehicle_Date",
                schema: "fleet",
                table: "VehicleCustodyEntry",
                columns: new[] { "VehicleId", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "UX_VehicleCustodyEntry_Source",
                schema: "fleet",
                table: "VehicleCustodyEntry",
                columns: new[] { "SourceType", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDriverAssignment_Driver_Start",
                schema: "fleet",
                table: "VehicleDriverAssignment",
                columns: new[] { "DriverEmployeeId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDriverAssignment_Vehicle_Start",
                schema: "fleet",
                table: "VehicleDriverAssignment",
                columns: new[] { "VehicleId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "UX_VehicleDriverAssignment_Open",
                schema: "fleet",
                table: "VehicleDriverAssignment",
                column: "VehicleId",
                unique: true,
                filter: "[EndDate] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleExpense_DriverAssignmentId",
                schema: "fleet",
                table: "VehicleExpense",
                column: "DriverAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleExpense_DriverEmployeeId",
                schema: "fleet",
                table: "VehicleExpense",
                column: "DriverEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleExpense_InvoiceNo",
                schema: "fleet",
                table: "VehicleExpense",
                column: "InvoiceNo",
                filter: "[InvoiceNo] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleExpense_Project_Date",
                schema: "fleet",
                table: "VehicleExpense",
                columns: new[] { "ProjectId", "ExpenseDate" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleExpense_Status",
                schema: "fleet",
                table: "VehicleExpense",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleExpense_Vehicle_Date",
                schema: "fleet",
                table: "VehicleExpense",
                columns: new[] { "VehicleId", "ExpenseDate" });

            migrationBuilder.CreateIndex(
                name: "UX_VehicleExpense_CustodyEntry",
                schema: "fleet",
                table: "VehicleExpense",
                column: "CustodyEntryId",
                unique: true,
                filter: "[CustodyEntryId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_VehicleExpense_IdempotencyKey",
                schema: "fleet",
                table: "VehicleExpense",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_VehicleExpense_Number",
                schema: "fleet",
                table: "VehicleExpense",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_VehicleExpenseAlert_Expense_Recipient",
                schema: "fleet",
                table: "VehicleExpenseAlert",
                columns: new[] { "ExpenseId", "RecipientUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleExpenseFlag_ExpenseId",
                schema: "fleet",
                table: "VehicleExpenseFlag",
                column: "ExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleViolation_DriverAssignmentId",
                schema: "fleet",
                table: "VehicleViolation",
                column: "DriverAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleViolation_DriverEmployeeId",
                schema: "fleet",
                table: "VehicleViolation",
                column: "DriverEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleViolation_Status",
                schema: "fleet",
                table: "VehicleViolation",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleViolation_Vehicle_Date",
                schema: "fleet",
                table: "VehicleViolation",
                columns: new[] { "VehicleId", "ViolationDate" });

            migrationBuilder.CreateIndex(
                name: "UX_VehicleViolation_Number",
                schema: "fleet",
                table: "VehicleViolation",
                column: "Number",
                unique: true);

            // Written by hand: other modules own these tables (HR employees, Projects projects and suppliers, Access users).
            foreach (var (name, table) in new[]
                     {
                         (VehicleDriverAssignmentConfiguration.DriverForeignKey, "VehicleDriverAssignment"),
                         (VehicleCustodyEntryConfiguration.DriverForeignKey, "VehicleCustodyEntry"),
                         (VehicleExpenseConfiguration.DriverForeignKey, "VehicleExpense"),
                         (VehicleViolationConfiguration.DriverForeignKey, "VehicleViolation"),
                     })
            {
                migrationBuilder.AddForeignKey(
                    name: name,
                    schema: "fleet",
                    table: table,
                    column: "DriverEmployeeId",
                    principalSchema: "hr",
                    principalTable: "Employee",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.NoAction);
            }

            migrationBuilder.AddForeignKey(
                name: VehicleExpenseConfiguration.ProjectForeignKey,
                schema: "fleet",
                table: "VehicleExpense",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: VehicleExpenseConfiguration.SupplierForeignKey,
                schema: "fleet",
                table: "VehicleExpense",
                column: "SupplierId",
                principalSchema: "projects",
                principalTable: "Supplier",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: VehicleExpenseAlertConfiguration.RecipientForeignKey,
                schema: "fleet",
                table: "VehicleExpenseAlert",
                column: "RecipientUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: VehicleDriverAssignmentConfiguration.DriverForeignKey, schema: "fleet", table: "VehicleDriverAssignment");
            migrationBuilder.DropForeignKey(name: VehicleCustodyEntryConfiguration.DriverForeignKey, schema: "fleet", table: "VehicleCustodyEntry");
            migrationBuilder.DropForeignKey(name: VehicleExpenseConfiguration.DriverForeignKey, schema: "fleet", table: "VehicleExpense");
            migrationBuilder.DropForeignKey(name: VehicleViolationConfiguration.DriverForeignKey, schema: "fleet", table: "VehicleViolation");
            migrationBuilder.DropForeignKey(name: VehicleExpenseConfiguration.ProjectForeignKey, schema: "fleet", table: "VehicleExpense");
            migrationBuilder.DropForeignKey(name: VehicleExpenseConfiguration.SupplierForeignKey, schema: "fleet", table: "VehicleExpense");
            migrationBuilder.DropForeignKey(name: VehicleExpenseAlertConfiguration.RecipientForeignKey, schema: "fleet", table: "VehicleExpenseAlert");
            migrationBuilder.DropTable(
                name: "FleetAttachment",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehicleExpenseAlert",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehicleExpenseFlag",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehicleViolation",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehicleExpense",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehicleCustodyEntry",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehicleDriverAssignment",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "Vehicle",
                schema: "fleet");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Equipment_Category",
                schema: "fleet",
                table: "Equipment");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Equipment_Category",
                schema: "fleet",
                table: "Equipment",
                sql: "[Category] IN ('Tools', 'Medium', 'Heavy', 'Survey', 'Safety')");
        }
    }
}
