using System;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Fleet.Data.Migrations
{
    /// <inheritdoc />
    internal partial class InitialFleet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fleet");

            migrationBuilder.CreateTable(
                name: "Equipment",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetNo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    TypeName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SerialNo = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Ownership = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Condition = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Equipment", x => x.Id);
                    table.CheckConstraint("CK_Equipment_Category", "[Category] IN ('Tools', 'Medium', 'Heavy', 'Survey', 'Safety')");
                    table.CheckConstraint("CK_Equipment_Condition", "[Condition] IN ('New', 'Good', 'Used', 'Damaged')");
                    table.CheckConstraint("CK_Equipment_Ownership", "[Ownership] IN ('Owned', 'Rented')");
                    table.CheckConstraint("CK_Equipment_Status", "[Status] IN ('InService', 'UnderMaintenance', 'Lost', 'Retired')");
                });

            migrationBuilder.CreateTable(
                name: "EquipmentAssignment",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EquipmentId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    ResponsibleUserId = table.Column<int>(type: "int", nullable: true),
                    OperatorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    AssignedBy = table.Column<int>(type: "int", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    EndedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquipmentAssignment", x => x.Id);
                    table.CheckConstraint("CK_EquipmentAssignment_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
                    table.CheckConstraint("CK_EquipmentAssignment_End", "([EndDate] IS NULL AND [EndedAt] IS NULL AND [EndedBy] IS NULL) OR ([EndDate] IS NOT NULL AND [EndedAt] IS NOT NULL AND [EndedBy] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_EquipmentAssignment_Equipment_EquipmentId",
                        column: x => x.EquipmentId,
                        principalSchema: "fleet",
                        principalTable: "Equipment",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "EquipmentCost",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EquipmentId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    CostDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Type = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Basis = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    Rate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Liters = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    PricePerLiter = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: true),
                    Channel = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    InvoiceNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: true),
                    PaymentMethod = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    PaymentReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PaymentRecordedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    PaymentRecordedBy = table.Column<int>(type: "int", nullable: true),
                    PaymentVoidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    PaymentVoidedBy = table.Column<int>(type: "int", nullable: true),
                    PaymentVoidReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquipmentCost", x => x.Id);
                    table.CheckConstraint("CK_EquipmentCost_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_EquipmentCost_Basis", "[Basis] IS NULL OR [Basis] IN ('Day', 'Month', 'Hour', 'Lump')");
                    table.CheckConstraint("CK_EquipmentCost_Channel", "[Channel] IN ('Custody', 'Credit')");
                    table.CheckConstraint("CK_EquipmentCost_CreditSupplier", "[Channel] = 'Custody' OR [SupplierId] IS NOT NULL");
                    table.CheckConstraint("CK_EquipmentCost_Payment", "([PaidOn] IS NULL AND [PaymentMethod] IS NULL AND [PaymentReference] IS NULL AND [PaymentRecordedAt] IS NULL AND [PaymentRecordedBy] IS NULL) OR ([PaidOn] IS NOT NULL AND [PaymentMethod] IS NOT NULL AND [PaymentRecordedAt] IS NOT NULL AND [PaymentRecordedBy] IS NOT NULL AND [Status] = 'Approved' AND [Channel] = 'Credit')");
                    table.CheckConstraint("CK_EquipmentCost_PaymentMethod", "[PaymentMethod] IS NULL OR [PaymentMethod] IN ('BankTransfer', 'Cheque', 'Cash')");
                    table.CheckConstraint("CK_EquipmentCost_PaymentVoid", "([PaymentVoidedAt] IS NULL AND [PaymentVoidedBy] IS NULL AND [PaymentVoidReason] IS NULL) OR ([PaymentVoidedAt] IS NOT NULL AND [PaymentVoidedBy] IS NOT NULL AND [PaymentVoidReason] IS NOT NULL)");
                    table.CheckConstraint("CK_EquipmentCost_RentalSupplier", "[Type] <> 'Rental' OR [SupplierId] IS NOT NULL");
                    table.CheckConstraint("CK_EquipmentCost_Review", "([Status] = 'Recorded' AND [ReviewedAt] IS NULL AND [ReviewedBy] IS NULL) OR ([Status] <> 'Recorded' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_EquipmentCost_Status", "[Status] IN ('Recorded', 'Approved', 'Returned', 'Rejected')");
                    table.CheckConstraint("CK_EquipmentCost_Type", "[Type] IN ('Rental', 'Fuel', 'Maintenance', 'Repair', 'Transport', 'Other')");
                    table.CheckConstraint("CK_EquipmentCost_TypeColumns", "([Type] = 'Fuel' AND [Liters] IS NOT NULL AND [PricePerLiter] IS NOT NULL AND [Liters] > 0 AND [PricePerLiter] > 0 AND [Basis] IS NULL AND [Quantity] IS NULL AND [Rate] IS NULL AND ABS([Amount] - ROUND([Liters] * [PricePerLiter], 2)) < 0.01) OR ([Type] <> 'Fuel' AND [Liters] IS NULL AND [PricePerLiter] IS NULL AND [Basis] IS NOT NULL AND (([Basis] = 'Lump' AND [Quantity] IS NULL AND [Rate] IS NULL) OR ([Basis] <> 'Lump' AND [Quantity] IS NOT NULL AND [Rate] IS NOT NULL AND [Quantity] > 0 AND [Rate] > 0 AND ABS([Amount] - ROUND([Quantity] * [Rate], 2)) < 0.01)))");
                    table.ForeignKey(
                        name: "FK_EquipmentCost_Equipment_EquipmentId",
                        column: x => x.EquipmentId,
                        principalSchema: "fleet",
                        principalTable: "Equipment",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "EquipmentRental",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EquipmentId = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: false),
                    RateBasis = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquipmentRental", x => x.Id);
                    table.CheckConstraint("CK_EquipmentRental_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
                    table.CheckConstraint("CK_EquipmentRental_Rate", "[Rate] > 0");
                    table.CheckConstraint("CK_EquipmentRental_RateBasis", "[RateBasis] IN ('Day', 'Month', 'Hour')");
                    table.ForeignKey(
                        name: "FK_EquipmentRental_Equipment_EquipmentId",
                        column: x => x.EquipmentId,
                        principalSchema: "fleet",
                        principalTable: "Equipment",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VoidedEquipmentCostPayment",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CostId = table.Column<int>(type: "int", nullable: false),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PaymentMethod = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    PaymentReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PaymentRecordedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    PaymentRecordedBy = table.Column<int>(type: "int", nullable: false),
                    VoidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    VoidedBy = table.Column<int>(type: "int", nullable: false),
                    VoidReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoidedEquipmentCostPayment", x => x.Id);
                    table.CheckConstraint("CK_VoidedEquipmentCostPayment_PaymentMethod", "[PaymentMethod] IN ('BankTransfer', 'Cheque', 'Cash')");
                    table.ForeignKey(
                        name: "FK_VoidedEquipmentCostPayment_EquipmentCost_CostId",
                        column: x => x.CostId,
                        principalSchema: "fleet",
                        principalTable: "EquipmentCost",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Equipment_Status_Category",
                schema: "fleet",
                table: "Equipment",
                columns: new[] { "Status", "Category" });

            migrationBuilder.CreateIndex(
                name: "UX_Equipment_AssetNo",
                schema: "fleet",
                table: "Equipment",
                column: "AssetNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentAssignment_Equipment_Start",
                schema: "fleet",
                table: "EquipmentAssignment",
                columns: new[] { "EquipmentId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentAssignment_Project_End",
                schema: "fleet",
                table: "EquipmentAssignment",
                columns: new[] { "ProjectId", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentAssignment_ResponsibleUserId",
                schema: "fleet",
                table: "EquipmentAssignment",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "UX_EquipmentAssignment_Open",
                schema: "fleet",
                table: "EquipmentAssignment",
                column: "EquipmentId",
                unique: true,
                filter: "[EndDate] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentCost_EquipmentId",
                schema: "fleet",
                table: "EquipmentCost",
                column: "EquipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentCost_Project_Date",
                schema: "fleet",
                table: "EquipmentCost",
                columns: new[] { "ProjectId", "CostDate" });

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentCost_Project_Status",
                schema: "fleet",
                table: "EquipmentCost",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentCost_SupplierId",
                schema: "fleet",
                table: "EquipmentCost",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentRental_EquipmentId",
                schema: "fleet",
                table: "EquipmentRental",
                column: "EquipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentRental_SupplierId",
                schema: "fleet",
                table: "EquipmentRental",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "UX_EquipmentRental_Open",
                schema: "fleet",
                table: "EquipmentRental",
                column: "EquipmentId",
                unique: true,
                filter: "[EndDate] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_VoidedEquipmentCostPayment_CostId",
                schema: "fleet",
                table: "VoidedEquipmentCostPayment",
                column: "CostId");

            // Written by hand: projects.Project, projects.Supplier and access.User belong to other modules, so they
            // aren't in this module's model. Projects, suppliers and users are never deleted, so these keys never
            // block; they stop a row that points to something that doesn't exist. The Access and Projects
            // migrations must be applied before this one.
            migrationBuilder.AddForeignKey(
                name: EquipmentRentalConfiguration.SupplierForeignKey,
                schema: "fleet",
                table: "EquipmentRental",
                column: "SupplierId",
                principalSchema: "projects",
                principalTable: "Supplier",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: EquipmentAssignmentConfiguration.ProjectForeignKey,
                schema: "fleet",
                table: "EquipmentAssignment",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: EquipmentAssignmentConfiguration.ResponsibleUserForeignKey,
                schema: "fleet",
                table: "EquipmentAssignment",
                column: "ResponsibleUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: EquipmentCostConfiguration.ProjectForeignKey,
                schema: "fleet",
                table: "EquipmentCost",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: EquipmentCostConfiguration.SupplierForeignKey,
                schema: "fleet",
                table: "EquipmentCost",
                column: "SupplierId",
                principalSchema: "projects",
                principalTable: "Supplier",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: EquipmentRentalConfiguration.SupplierForeignKey,
                schema: "fleet",
                table: "EquipmentRental");

            migrationBuilder.DropForeignKey(
                name: EquipmentAssignmentConfiguration.ProjectForeignKey,
                schema: "fleet",
                table: "EquipmentAssignment");

            migrationBuilder.DropForeignKey(
                name: EquipmentAssignmentConfiguration.ResponsibleUserForeignKey,
                schema: "fleet",
                table: "EquipmentAssignment");

            migrationBuilder.DropForeignKey(
                name: EquipmentCostConfiguration.ProjectForeignKey,
                schema: "fleet",
                table: "EquipmentCost");

            migrationBuilder.DropForeignKey(
                name: EquipmentCostConfiguration.SupplierForeignKey,
                schema: "fleet",
                table: "EquipmentCost");

            migrationBuilder.DropTable(
                name: "EquipmentAssignment",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "EquipmentRental",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VoidedEquipmentCostPayment",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "EquipmentCost",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "Equipment",
                schema: "fleet");
        }
    }
}
