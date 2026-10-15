using System;
using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <inheritdoc />
    internal partial class ProjectWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Budget",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ApprovedBy = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Budget", x => x.Id);
                    table.CheckConstraint("CK_Budget_Approval", "([Status] = 'Draft' AND [ApprovedAt] IS NULL AND [ApprovedBy] IS NULL) OR ([Status] <> 'Draft' AND [ApprovedAt] IS NOT NULL AND [ApprovedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_Budget_Status", "[Status] IN ('Draft', 'Approved', 'Superseded')");
                    table.CheckConstraint("CK_Budget_Version", "[Version] >= 1");
                    table.ForeignKey(
                        name: "FK_Budget_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProjectItem",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Unit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    DeactivatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeactivatedBy = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectItem", x => x.Id);
                    table.CheckConstraint("CK_ProjectItem_Category", "[Category] IN ('Concrete', 'SteelFixing', 'Formwork', 'Brickwork', 'Plastering', 'Tiling', 'Painting', 'Excavation', 'Waterproofing', 'Piling', 'Mep', 'Guarding', 'EquipmentOperation', 'General')");
                    table.CheckConstraint("CK_ProjectItem_Deactivation", "([Status] = 'Active' AND [DeactivatedAt] IS NULL AND [DeactivatedBy] IS NULL) OR ([Status] = 'Inactive' AND [DeactivatedAt] IS NOT NULL AND [DeactivatedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_ProjectItem_Status", "[Status] IN ('Active', 'Inactive')");
                    table.CheckConstraint("CK_ProjectItem_Unit", "[Unit] IN ('Bag', 'Ton', 'M3', 'M2', 'M', 'Piece', 'Trip', 'Roll', 'Lot', 'Job', 'Day', 'Visit', 'Brick', 'Liter', 'Kg', 'Other')");
                    table.ForeignKey(
                        name: "FK_ProjectItem_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ReportRequest",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    RequesterUserId = table.Column<int>(type: "int", nullable: false),
                    RecipientUserId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Priority = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    IsProactive = table.Column<bool>(type: "bit", nullable: false),
                    ResponseText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReturnNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportRequest", x => x.Id);
                    table.CheckConstraint("CK_ReportRequest_Answer", "[Status] NOT IN ('Submitted', 'Completed') OR ([SubmittedAt] IS NOT NULL AND [ResponseText] IS NOT NULL)");
                    table.CheckConstraint("CK_ReportRequest_Cancel", "[Status] <> 'Cancelled' OR ([CancelledAt] IS NOT NULL AND [CancelNote] IS NOT NULL)");
                    table.CheckConstraint("CK_ReportRequest_Parties", "[RequesterUserId] <> [RecipientUserId]");
                    table.CheckConstraint("CK_ReportRequest_Priority", "[Priority] IN ('Normal', 'High', 'Urgent')");
                    table.CheckConstraint("CK_ReportRequest_Proactive", "[IsProactive] = 0 OR [ResponseText] IS NOT NULL");
                    table.CheckConstraint("CK_ReportRequest_Return", "[Status] <> 'Returned' OR [ReturnNote] IS NOT NULL");
                    table.CheckConstraint("CK_ReportRequest_Review", "[Status] NOT IN ('Completed', 'Returned') OR ([ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_ReportRequest_Status", "[Status] IN ('Requested', 'InProgress', 'Submitted', 'Returned', 'Completed', 'Cancelled')");
                    table.CheckConstraint("CK_ReportRequest_Type", "[Type] IN ('DailyReport', 'Labor', 'Materials', 'Subcontractor', 'Expense', 'Equipment', 'Other')");
                    table.ForeignKey(
                        name: "FK_ReportRequest_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Supplier",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Phone = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    TaxNumber = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
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
                    table.PrimaryKey("PK_Supplier", x => x.Id);
                    table.CheckConstraint("CK_Supplier_Kind", "[Kind] IN ('Materials', 'Equipment', 'Services', 'Fuel', 'Other')");
                    table.CheckConstraint("CK_Supplier_Status", "[Status] IN ('Active', 'Inactive')");
                });

            migrationBuilder.CreateTable(
                name: "BudgetLine",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BudgetId = table.Column<int>(type: "int", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    CostGroup = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ProjectItemId = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Unit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetLine", x => x.Id);
                    table.CheckConstraint("CK_BudgetLine_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_BudgetLine_CostGroup", "[CostGroup] IN ('Labor', 'Subcontractors', 'Materials', 'Equipment', 'SiteExpenses', 'Other')");
                    table.CheckConstraint("CK_BudgetLine_LineNo", "[LineNo] >= 1");
                    table.CheckConstraint("CK_BudgetLine_Pricing", "([Quantity] IS NULL AND [UnitPrice] IS NULL) OR ([Quantity] IS NOT NULL AND [UnitPrice] IS NOT NULL AND [Quantity] > 0 AND [UnitPrice] > 0 AND ABS([Amount] - ROUND([Quantity] * [UnitPrice], 2)) < 0.01)");
                    table.CheckConstraint("CK_BudgetLine_Unit", "[Unit] IS NULL OR [Unit] IN ('Bag', 'Ton', 'M3', 'M2', 'M', 'Piece', 'Trip', 'Roll', 'Lot', 'Job', 'Day', 'Visit', 'Brick', 'Liter', 'Kg', 'Other')");
                    table.ForeignKey(
                        name: "FK_BudgetLine_Budget_BudgetId",
                        column: x => x.BudgetId,
                        principalSchema: "projects",
                        principalTable: "Budget",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_BudgetLine_ProjectItem_ProjectItemId",
                        column: x => x.ProjectItemId,
                        principalSchema: "projects",
                        principalTable: "ProjectItem",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DailyReport",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    ReportDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AuthorUserId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    WorkersPresent = table.Column<int>(type: "int", nullable: true),
                    Weather = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Issues = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReportRequestId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyReport", x => x.Id);
                    table.CheckConstraint("CK_DailyReport_Review", "[Status] = 'Submitted' OR ([Status] = 'Reviewed' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL) OR ([Status] = 'Returned' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL AND [ReviewNote] IS NOT NULL)");
                    table.CheckConstraint("CK_DailyReport_Status", "[Status] IN ('Submitted', 'Reviewed', 'Returned')");
                    table.CheckConstraint("CK_DailyReport_WorkersPresent", "[WorkersPresent] IS NULL OR [WorkersPresent] >= 0");
                    table.ForeignKey(
                        name: "FK_DailyReport_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DailyReport_ReportRequest_ReportRequestId",
                        column: x => x.ReportRequestId,
                        principalSchema: "projects",
                        principalTable: "ReportRequest",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CatalogItem",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Unit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: true),
                    EstimatedQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogItem", x => x.Id);
                    table.CheckConstraint("CK_CatalogItem_Category", "[Category] IN ('CementAggregates', 'ReadyMix', 'Steel', 'Masonry', 'Finishing', 'Waterproofing', 'Piling', 'Formwork', 'Consumables', 'Services', 'Other')");
                    table.CheckConstraint("CK_CatalogItem_EstimatedQuantity", "[EstimatedQuantity] >= 0");
                    table.CheckConstraint("CK_CatalogItem_Status", "[Status] IN ('Active', 'Inactive')");
                    table.CheckConstraint("CK_CatalogItem_Unit", "[Unit] IN ('Bag', 'Ton', 'M3', 'M2', 'M', 'Piece', 'Trip', 'Roll', 'Lot', 'Job', 'Day', 'Visit', 'Brick', 'Liter', 'Kg', 'Other')");
                    table.ForeignKey(
                        name: "FK_CatalogItem_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CatalogItem_Supplier_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "projects",
                        principalTable: "Supplier",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SupplierPayment",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
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
                    table.PrimaryKey("PK_SupplierPayment", x => x.Id);
                    table.CheckConstraint("CK_SupplierPayment_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_SupplierPayment_Method", "[Method] IN ('BankTransfer', 'Cheque', 'Cash')");
                    table.CheckConstraint("CK_SupplierPayment_Void", "([VoidedAt] IS NULL AND [VoidedBy] IS NULL AND [VoidReason] IS NULL) OR ([VoidedAt] IS NOT NULL AND [VoidedBy] IS NOT NULL AND [VoidReason] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_SupplierPayment_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SupplierPayment_Supplier_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "projects",
                        principalTable: "Supplier",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DailyReportEquipment",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportId = table.Column<int>(type: "int", nullable: false),
                    EquipmentId = table.Column<int>(type: "int", nullable: true),
                    EquipmentName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Hours = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyReportEquipment", x => x.Id);
                    table.CheckConstraint("CK_DailyReportEquipment_Hours", "[Hours] IS NULL OR ([Hours] > 0 AND [Hours] <= 24)");
                    table.ForeignKey(
                        name: "FK_DailyReportEquipment_DailyReport_ReportId",
                        column: x => x.ReportId,
                        principalSchema: "projects",
                        principalTable: "DailyReport",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DailyReportLine",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportId = table.Column<int>(type: "int", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    WorkType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Unit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    PerformedBy = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ProjectItemId = table.Column<int>(type: "int", nullable: true),
                    SubcontractorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SubContractId = table.Column<int>(type: "int", nullable: true),
                    WorkersCount = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyReportLine", x => x.Id);
                    table.CheckConstraint("CK_DailyReportLine_LineNo", "[LineNo] >= 1");
                    table.CheckConstraint("CK_DailyReportLine_PerformedBy", "[PerformedBy] IN ('Internal', 'Subcontractor')");
                    table.CheckConstraint("CK_DailyReportLine_Quantity", "[Quantity] > 0");
                    table.CheckConstraint("CK_DailyReportLine_Subcontractor", "([PerformedBy] = 'Internal' AND [SubcontractorName] IS NULL AND [SubContractId] IS NULL) OR ([PerformedBy] = 'Subcontractor' AND ([SubcontractorName] IS NOT NULL OR [SubContractId] IS NOT NULL))");
                    table.CheckConstraint("CK_DailyReportLine_Unit", "[Unit] IN ('Bag', 'Ton', 'M3', 'M2', 'M', 'Piece', 'Trip', 'Roll', 'Lot', 'Job', 'Day', 'Visit', 'Brick', 'Liter', 'Kg', 'Other')");
                    table.CheckConstraint("CK_DailyReportLine_WorkersCount", "[WorkersCount] IS NULL OR [WorkersCount] >= 0");
                    table.CheckConstraint("CK_DailyReportLine_WorkType", "[WorkType] IN ('Concrete', 'SteelFixing', 'Formwork', 'Brickwork', 'Plastering', 'Tiling', 'Painting', 'Excavation', 'Waterproofing', 'Piling', 'Mep', 'Guarding', 'EquipmentOperation', 'General')");
                    table.ForeignKey(
                        name: "FK_DailyReportLine_DailyReport_ReportId",
                        column: x => x.ReportId,
                        principalSchema: "projects",
                        principalTable: "DailyReport",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DailyReportLine_ProjectItem_ProjectItemId",
                        column: x => x.ProjectItemId,
                        principalSchema: "projects",
                        principalTable: "ProjectItem",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DailyReportMaterial",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportId = table.Column<int>(type: "int", nullable: false),
                    MaterialName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Unit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyReportMaterial", x => x.Id);
                    table.CheckConstraint("CK_DailyReportMaterial_Quantity", "[Quantity] > 0");
                    table.CheckConstraint("CK_DailyReportMaterial_Unit", "[Unit] IN ('Bag', 'Ton', 'M3', 'M2', 'M', 'Piece', 'Trip', 'Roll', 'Lot', 'Job', 'Day', 'Visit', 'Brick', 'Liter', 'Kg', 'Other')");
                    table.ForeignKey(
                        name: "FK_DailyReportMaterial_DailyReport_ReportId",
                        column: x => x.ReportId,
                        principalSchema: "projects",
                        principalTable: "DailyReport",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ItemPrice",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CatalogItemId = table.Column<int>(type: "int", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PreviousPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RequestedBy = table.Column<int>(type: "int", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecidedBy = table.Column<int>(type: "int", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemPrice", x => x.Id);
                    table.CheckConstraint("CK_ItemPrice_Decision", "([Status] = 'Pending' AND [DecidedAt] IS NULL AND [DecidedBy] IS NULL) OR ([Status] <> 'Pending' AND [DecidedAt] IS NOT NULL AND [DecidedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_ItemPrice_Price", "[Price] > 0 AND ([PreviousPrice] IS NULL OR [PreviousPrice] > 0)");
                    table.CheckConstraint("CK_ItemPrice_Status", "[Status] IN ('Pending', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_ItemPrice_CatalogItem_CatalogItemId",
                        column: x => x.CatalogItemId,
                        principalSchema: "projects",
                        principalTable: "CatalogItem",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProjectExpense",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ExpenseDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: true),
                    ItemName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Unit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Category = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Channel = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    InvoiceNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DeliveryNoteNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CatalogItemId = table.Column<int>(type: "int", nullable: true),
                    CatalogPriceAtRecording = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ProjectItemId = table.Column<int>(type: "int", nullable: true),
                    ItemLinkedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ItemLinkedBy = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectExpense", x => x.Id);
                    table.CheckConstraint("CK_ProjectExpense_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_ProjectExpense_Category", "[Category] IS NULL OR [Category] IN ('FeesTransport', 'Lab', 'Survey', 'Consumables', 'PieceWork', 'Guards', 'Accommodation', 'PettyCash', 'Permits', 'Other')");
                    table.CheckConstraint("CK_ProjectExpense_Channel", "[Channel] IN ('Custody', 'Credit')");
                    table.CheckConstraint("CK_ProjectExpense_CreditSupplier", "[Channel] = 'Custody' OR [SupplierId] IS NOT NULL");
                    table.CheckConstraint("CK_ProjectExpense_ItemLink", "([ItemLinkedAt] IS NULL AND [ItemLinkedBy] IS NULL) OR ([ItemLinkedAt] IS NOT NULL AND [ItemLinkedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_ProjectExpense_Kind", "[Kind] IN ('Supply', 'Expense')");
                    table.CheckConstraint("CK_ProjectExpense_KindColumns", "([Kind] = 'Supply' AND [SupplierId] IS NOT NULL AND [ItemName] IS NOT NULL AND [Unit] IS NOT NULL AND [Quantity] IS NOT NULL AND [UnitPrice] IS NOT NULL AND [Quantity] > 0 AND [UnitPrice] > 0 AND [Category] IS NULL AND ABS([Amount] - ROUND([Quantity] * [UnitPrice], 2)) < 0.01) OR ([Kind] = 'Expense' AND [Category] IS NOT NULL AND [Description] IS NOT NULL AND [ItemName] IS NULL AND [Unit] IS NULL AND [Quantity] IS NULL AND [UnitPrice] IS NULL)");
                    table.CheckConstraint("CK_ProjectExpense_Review", "([Status] = 'Recorded' AND [ReviewedAt] IS NULL AND [ReviewedBy] IS NULL) OR ([Status] <> 'Recorded' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_ProjectExpense_Status", "[Status] IN ('Recorded', 'Approved', 'Returned', 'Rejected')");
                    table.CheckConstraint("CK_ProjectExpense_Unit", "[Unit] IS NULL OR [Unit] IN ('Bag', 'Ton', 'M3', 'M2', 'M', 'Piece', 'Trip', 'Roll', 'Lot', 'Job', 'Day', 'Visit', 'Brick', 'Liter', 'Kg', 'Other')");
                    table.ForeignKey(
                        name: "FK_ProjectExpense_CatalogItem_CatalogItemId",
                        column: x => x.CatalogItemId,
                        principalSchema: "projects",
                        principalTable: "CatalogItem",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProjectExpense_ProjectItem_ProjectItemId",
                        column: x => x.ProjectItemId,
                        principalSchema: "projects",
                        principalTable: "ProjectItem",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProjectExpense_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProjectExpense_Supplier_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "projects",
                        principalTable: "Supplier",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SupplierPaymentAllocation",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PaymentId = table.Column<int>(type: "int", nullable: false),
                    ExpenseId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPaymentAllocation", x => x.Id);
                    table.CheckConstraint("CK_SupplierPaymentAllocation_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_SupplierPaymentAllocation_ProjectExpense_ExpenseId",
                        column: x => x.ExpenseId,
                        principalSchema: "projects",
                        principalTable: "ProjectExpense",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SupplierPaymentAllocation_SupplierPayment_PaymentId",
                        column: x => x.PaymentId,
                        principalSchema: "projects",
                        principalTable: "SupplierPayment",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "UX_Budget_Approved",
                schema: "projects",
                table: "Budget",
                column: "ProjectId",
                unique: true,
                filter: "[Status] = 'Approved'");

            migrationBuilder.CreateIndex(
                name: "UX_Budget_Draft",
                schema: "projects",
                table: "Budget",
                column: "ProjectId",
                unique: true,
                filter: "[Status] = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "UX_Budget_Project_Version",
                schema: "projects",
                table: "Budget",
                columns: new[] { "ProjectId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetLine_ProjectItemId",
                schema: "projects",
                table: "BudgetLine",
                column: "ProjectItemId");

            migrationBuilder.CreateIndex(
                name: "UX_BudgetLine_Budget_LineNo",
                schema: "projects",
                table: "BudgetLine",
                columns: new[] { "BudgetId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItem_SupplierId",
                schema: "projects",
                table: "CatalogItem",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "UX_CatalogItem_NameAr",
                schema: "projects",
                table: "CatalogItem",
                columns: new[] { "ProjectId", "NameAr" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CatalogItem_NameEn",
                schema: "projects",
                table: "CatalogItem",
                columns: new[] { "ProjectId", "NameEn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyReport_Project_Status",
                schema: "projects",
                table: "DailyReport",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyReport_ReportRequestId",
                schema: "projects",
                table: "DailyReport",
                column: "ReportRequestId");

            migrationBuilder.CreateIndex(
                name: "UX_DailyReport_Project_Date_Author",
                schema: "projects",
                table: "DailyReport",
                columns: new[] { "ProjectId", "ReportDate", "AuthorUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyReportEquipment_ReportId",
                schema: "projects",
                table: "DailyReportEquipment",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_DailyReportLine_ProjectItemId",
                schema: "projects",
                table: "DailyReportLine",
                column: "ProjectItemId");

            migrationBuilder.CreateIndex(
                name: "UX_DailyReportLine_Report_LineNo",
                schema: "projects",
                table: "DailyReportLine",
                columns: new[] { "ReportId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyReportMaterial_ReportId",
                schema: "projects",
                table: "DailyReportMaterial",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "UX_ItemPrice_ApprovedFrom",
                schema: "projects",
                table: "ItemPrice",
                columns: new[] { "CatalogItemId", "EffectiveFrom" },
                unique: true,
                filter: "[Status] = 'Approved'");

            migrationBuilder.CreateIndex(
                name: "UX_ItemPrice_Pending",
                schema: "projects",
                table: "ItemPrice",
                column: "CatalogItemId",
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectExpense_CatalogItemId",
                schema: "projects",
                table: "ProjectExpense",
                column: "CatalogItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectExpense_Project_Date",
                schema: "projects",
                table: "ProjectExpense",
                columns: new[] { "ProjectId", "ExpenseDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectExpense_Project_Status",
                schema: "projects",
                table: "ProjectExpense",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectExpense_ProjectItemId",
                schema: "projects",
                table: "ProjectExpense",
                column: "ProjectItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectExpense_SupplierId",
                schema: "projects",
                table: "ProjectExpense",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "UX_ProjectItem_NameAr",
                schema: "projects",
                table: "ProjectItem",
                columns: new[] { "ProjectId", "NameAr" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ProjectItem_NameEn",
                schema: "projects",
                table: "ProjectItem",
                columns: new[] { "ProjectId", "NameEn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportRequest_Project_Status",
                schema: "projects",
                table: "ReportRequest",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportRequest_RecipientUserId",
                schema: "projects",
                table: "ReportRequest",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRequest_RequesterUserId",
                schema: "projects",
                table: "ReportRequest",
                column: "RequesterUserId");

            migrationBuilder.CreateIndex(
                name: "UX_Supplier_NameEn",
                schema: "projects",
                table: "Supplier",
                column: "NameEn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayment_Project_Date",
                schema: "projects",
                table: "SupplierPayment",
                columns: new[] { "ProjectId", "PaymentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayment_Project_Supplier",
                schema: "projects",
                table: "SupplierPayment",
                columns: new[] { "ProjectId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayment_SupplierId",
                schema: "projects",
                table: "SupplierPayment",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "UX_SupplierPayment_IdempotencyKey",
                schema: "projects",
                table: "SupplierPayment",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPaymentAllocation_ExpenseId",
                schema: "projects",
                table: "SupplierPaymentAllocation",
                column: "ExpenseId");

            migrationBuilder.CreateIndex(
                name: "UX_SupplierPaymentAllocation_Payment_Expense",
                schema: "projects",
                table: "SupplierPaymentAllocation",
                columns: new[] { "PaymentId", "ExpenseId" },
                unique: true);

            // Hand-written: the users table belongs to the Access module, so EF doesn't know about it.
            migrationBuilder.AddForeignKey(
                name: DailyReportConfiguration.AuthorForeignKey,
                schema: "projects",
                table: "DailyReport",
                column: "AuthorUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
            migrationBuilder.AddForeignKey(
                name: ReportRequestConfiguration.RequesterForeignKey,
                schema: "projects",
                table: "ReportRequest",
                column: "RequesterUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
            migrationBuilder.AddForeignKey(
                name: ReportRequestConfiguration.RecipientForeignKey,
                schema: "projects",
                table: "ReportRequest",
                column: "RecipientUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: DailyReportConfiguration.AuthorForeignKey,
                schema: "projects",
                table: "DailyReport");

            migrationBuilder.DropForeignKey(
                name: ReportRequestConfiguration.RequesterForeignKey,
                schema: "projects",
                table: "ReportRequest");

            migrationBuilder.DropForeignKey(
                name: ReportRequestConfiguration.RecipientForeignKey,
                schema: "projects",
                table: "ReportRequest");

            migrationBuilder.DropTable(
                name: "BudgetLine",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "DailyReportEquipment",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "DailyReportLine",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "DailyReportMaterial",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "ItemPrice",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "SupplierPaymentAllocation",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "Budget",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "DailyReport",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "ProjectExpense",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "SupplierPayment",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "ReportRequest",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "CatalogItem",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "ProjectItem",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "Supplier",
                schema: "projects");
        }
    }
}
