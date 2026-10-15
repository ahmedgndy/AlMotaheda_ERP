using System;
using AlMotaheda.Modules.Subcontractors.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Subcontractors.Data.Migrations
{
    /// <inheritdoc />
    internal partial class InitialSubcontractors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sub");

            migrationBuilder.CreateTable(
                name: "Subcontractor",
                schema: "sub",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Specialty = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ContactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    Email = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subcontractor", x => x.Id);
                    table.CheckConstraint("CK_Subcontractor_Specialty", "[Specialty] IN ('Civil', 'Masonry', 'Plumbing', 'Electrical', 'Mep', 'Finishing', 'Earthworks', 'Steel', 'Facade', 'Landscape', 'Other')");
                    table.CheckConstraint("CK_Subcontractor_Status", "[Status] IN ('Active', 'Inactive')");
                });

            migrationBuilder.CreateTable(
                name: "SubContract",
                schema: "sub",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubcontractorId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    ContractNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Model = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DailyRate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    RetentionPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubContract", x => x.Id);
                    table.CheckConstraint("CK_SubContract_DailyRate", "([Model] = 'DailyRate' AND [DailyRate] IS NOT NULL AND [DailyRate] > 0) OR ([Model] <> 'DailyRate' AND [DailyRate] IS NULL)");
                    table.CheckConstraint("CK_SubContract_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
                    table.CheckConstraint("CK_SubContract_Model", "[Model] IN ('UnitRate', 'LumpSum', 'DailyRate')");
                    table.CheckConstraint("CK_SubContract_Retention", "[RetentionPercent] >= 0 AND [RetentionPercent] <= 100");
                    table.CheckConstraint("CK_SubContract_Status", "[Status] IN ('Pending', 'Active', 'Completed')");
                    table.CheckConstraint("CK_SubContract_Value", "[Value] > 0");
                    table.ForeignKey(
                        name: "FK_SubContract_Subcontractor_SubcontractorId",
                        column: x => x.SubcontractorId,
                        principalSchema: "sub",
                        principalTable: "Subcontractor",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ContractItem",
                schema: "sub",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContractId = table.Column<int>(type: "int", nullable: false),
                    ProjectItemId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    WorkType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Unit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractItem", x => x.Id);
                    table.CheckConstraint("CK_ContractItem_Quantity", "[Quantity] > 0");
                    table.CheckConstraint("CK_ContractItem_Unit", "[Unit] IN ('Bag', 'Ton', 'M3', 'M2', 'M', 'Piece', 'Trip', 'Roll', 'Lot', 'Job', 'Day', 'Visit', 'Brick', 'Liter', 'Kg', 'Other')");
                    table.CheckConstraint("CK_ContractItem_UnitPrice", "[UnitPrice] > 0");
                    table.CheckConstraint("CK_ContractItem_WorkType", "[WorkType] IN ('Concrete', 'SteelFixing', 'Formwork', 'Brickwork', 'Plastering', 'Tiling', 'Painting', 'Excavation', 'Waterproofing', 'Piling', 'Mep', 'Guarding', 'EquipmentOperation', 'General')");
                    table.ForeignKey(
                        name: "FK_ContractItem_SubContract_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "sub",
                        principalTable: "SubContract",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ManpowerSheet",
                schema: "sub",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContractId = table.Column<int>(type: "int", nullable: false),
                    ProjectItemId = table.Column<int>(type: "int", nullable: true),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ToDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Workers = table.Column<int>(type: "int", nullable: false),
                    Days = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    DailyRate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    WorkType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManpowerSheet", x => x.Id);
                    table.CheckConstraint("CK_ManpowerSheet_Amount", "[Amount] > 0 AND ABS([Amount] - ROUND([Workers] * [Days] * [DailyRate], 2)) < 0.01");
                    table.CheckConstraint("CK_ManpowerSheet_Crew", "[Workers] > 0 AND [Days] > 0 AND [DailyRate] > 0");
                    table.CheckConstraint("CK_ManpowerSheet_Dates", "[ToDate] >= [FromDate]");
                    table.CheckConstraint("CK_ManpowerSheet_Review", "([Status] = 'Recorded' AND [ReviewedAt] IS NULL AND [ReviewedBy] IS NULL AND [ReviewNote] IS NULL) OR ([Status] = 'Approved' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL) OR ([Status] = 'Rejected' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL AND [ReviewNote] IS NOT NULL)");
                    table.CheckConstraint("CK_ManpowerSheet_Status", "[Status] IN ('Recorded', 'Approved', 'Rejected')");
                    table.CheckConstraint("CK_ManpowerSheet_WorkType", "[WorkType] IN ('Concrete', 'SteelFixing', 'Formwork', 'Brickwork', 'Plastering', 'Tiling', 'Painting', 'Excavation', 'Waterproofing', 'Piling', 'Mep', 'Guarding', 'EquipmentOperation', 'General')");
                    table.ForeignKey(
                        name: "FK_ManpowerSheet_SubContract_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "sub",
                        principalTable: "SubContract",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SubPayment",
                schema: "sub",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContractId = table.Column<int>(type: "int", nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Purpose = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VoidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    VoidedBy = table.Column<int>(type: "int", nullable: true),
                    VoidReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubPayment", x => x.Id);
                    table.CheckConstraint("CK_SubPayment_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_SubPayment_Method", "[Method] IN ('BankTransfer', 'Cheque', 'Cash')");
                    table.CheckConstraint("CK_SubPayment_Purpose", "[Purpose] IN ('Advance', 'Progress', 'Final', 'Retention')");
                    table.CheckConstraint("CK_SubPayment_Void", "([VoidedAt] IS NULL AND [VoidedBy] IS NULL AND [VoidReason] IS NULL) OR ([VoidedAt] IS NOT NULL AND [VoidedBy] IS NOT NULL AND [VoidReason] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_SubPayment_SubContract_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "sub",
                        principalTable: "SubContract",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProgressMeasurement",
                schema: "sub",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContractItemId = table.Column<int>(type: "int", nullable: false),
                    MeasuredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgressMeasurement", x => x.Id);
                    table.CheckConstraint("CK_ProgressMeasurement_Amount", "[Amount] > 0 AND ABS([Amount] - ROUND([Quantity] * [UnitPrice], 2)) < 0.01");
                    table.CheckConstraint("CK_ProgressMeasurement_Quantity", "[Quantity] > 0");
                    table.CheckConstraint("CK_ProgressMeasurement_Review", "([Status] = 'Recorded' AND [ReviewedAt] IS NULL AND [ReviewedBy] IS NULL AND [ReviewNote] IS NULL) OR ([Status] = 'Approved' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL) OR ([Status] = 'Rejected' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL AND [ReviewNote] IS NOT NULL)");
                    table.CheckConstraint("CK_ProgressMeasurement_Status", "[Status] IN ('Recorded', 'Approved', 'Rejected')");
                    table.CheckConstraint("CK_ProgressMeasurement_UnitPrice", "[UnitPrice] > 0");
                    table.ForeignKey(
                        name: "FK_ProgressMeasurement_ContractItem_ContractItemId",
                        column: x => x.ContractItemId,
                        principalSchema: "sub",
                        principalTable: "ContractItem",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SubPaymentAllocation",
                schema: "sub",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PaymentId = table.Column<int>(type: "int", nullable: false),
                    MeasurementId = table.Column<int>(type: "int", nullable: true),
                    ManpowerSheetId = table.Column<int>(type: "int", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubPaymentAllocation", x => x.Id);
                    table.CheckConstraint("CK_SubPaymentAllocation_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_SubPaymentAllocation_Work", "([MeasurementId] IS NOT NULL AND [ManpowerSheetId] IS NULL) OR ([MeasurementId] IS NULL AND [ManpowerSheetId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_SubPaymentAllocation_ManpowerSheet_ManpowerSheetId",
                        column: x => x.ManpowerSheetId,
                        principalSchema: "sub",
                        principalTable: "ManpowerSheet",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SubPaymentAllocation_ProgressMeasurement_MeasurementId",
                        column: x => x.MeasurementId,
                        principalSchema: "sub",
                        principalTable: "ProgressMeasurement",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SubPaymentAllocation_SubPayment_PaymentId",
                        column: x => x.PaymentId,
                        principalSchema: "sub",
                        principalTable: "SubPayment",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractItem_ContractId",
                schema: "sub",
                table: "ContractItem",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractItem_ProjectItemId",
                schema: "sub",
                table: "ContractItem",
                column: "ProjectItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ManpowerSheet_Contract_Status",
                schema: "sub",
                table: "ManpowerSheet",
                columns: new[] { "ContractId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ManpowerSheet_ProjectItemId",
                schema: "sub",
                table: "ManpowerSheet",
                column: "ProjectItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ProgressMeasurement_Item_Status",
                schema: "sub",
                table: "ProgressMeasurement",
                columns: new[] { "ContractItemId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SubContract_SubcontractorId",
                schema: "sub",
                table: "SubContract",
                column: "SubcontractorId");

            migrationBuilder.CreateIndex(
                name: "UX_SubContract_ContractNo",
                schema: "sub",
                table: "SubContract",
                column: "ContractNo",
                unique: true,
                filter: "[ContractNo] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_SubContract_Project_Subcontractor",
                schema: "sub",
                table: "SubContract",
                columns: new[] { "ProjectId", "SubcontractorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Subcontractor_NameEn",
                schema: "sub",
                table: "Subcontractor",
                column: "NameEn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubPayment_Contract_Date",
                schema: "sub",
                table: "SubPayment",
                columns: new[] { "ContractId", "PaymentDate" });

            migrationBuilder.CreateIndex(
                name: "UX_SubPayment_IdempotencyKey",
                schema: "sub",
                table: "SubPayment",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubPaymentAllocation_ManpowerSheetId",
                schema: "sub",
                table: "SubPaymentAllocation",
                column: "ManpowerSheetId");

            migrationBuilder.CreateIndex(
                name: "IX_SubPaymentAllocation_MeasurementId",
                schema: "sub",
                table: "SubPaymentAllocation",
                column: "MeasurementId");

            migrationBuilder.CreateIndex(
                name: "UX_SubPaymentAllocation_Payment_Manpower",
                schema: "sub",
                table: "SubPaymentAllocation",
                columns: new[] { "PaymentId", "ManpowerSheetId" },
                unique: true,
                filter: "[ManpowerSheetId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_SubPaymentAllocation_Payment_Measurement",
                schema: "sub",
                table: "SubPaymentAllocation",
                columns: new[] { "PaymentId", "MeasurementId" },
                unique: true,
                filter: "[MeasurementId] IS NOT NULL");

            // Written by hand: projects.Project and projects.ProjectItem belong to the Projects module, so they aren't
            // in this module's model. Projects and items are never deleted while work points to them, so the keys never
            // block; they stop a contract or a link to a project or item that doesn't exist.
            // The Access and Projects migrations must be applied before this one.
            migrationBuilder.AddForeignKey(
                name: SubContractConfiguration.ProjectForeignKey,
                schema: "sub",
                table: "SubContract",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: ContractItemConfiguration.ProjectItemForeignKey,
                schema: "sub",
                table: "ContractItem",
                column: "ProjectItemId",
                principalSchema: "projects",
                principalTable: "ProjectItem",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: ManpowerSheetConfiguration.ProjectItemForeignKey,
                schema: "sub",
                table: "ManpowerSheet",
                column: "ProjectItemId",
                principalSchema: "projects",
                principalTable: "ProjectItem",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: ManpowerSheetConfiguration.ProjectItemForeignKey,
                schema: "sub",
                table: "ManpowerSheet");

            migrationBuilder.DropForeignKey(
                name: ContractItemConfiguration.ProjectItemForeignKey,
                schema: "sub",
                table: "ContractItem");

            migrationBuilder.DropForeignKey(
                name: SubContractConfiguration.ProjectForeignKey,
                schema: "sub",
                table: "SubContract");

            migrationBuilder.DropTable(
                name: "SubPaymentAllocation",
                schema: "sub");

            migrationBuilder.DropTable(
                name: "ManpowerSheet",
                schema: "sub");

            migrationBuilder.DropTable(
                name: "ProgressMeasurement",
                schema: "sub");

            migrationBuilder.DropTable(
                name: "SubPayment",
                schema: "sub");

            migrationBuilder.DropTable(
                name: "ContractItem",
                schema: "sub");

            migrationBuilder.DropTable(
                name: "SubContract",
                schema: "sub");

            migrationBuilder.DropTable(
                name: "Subcontractor",
                schema: "sub");
        }
    }
}
