using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Procurement.Data.Migrations
{
    /// <inheritdoc />
    internal partial class InitialProcurement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "proc");

            migrationBuilder.CreateTable(
                name: "ProcurementPolicy",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    AutoApproveRequisitions = table.Column<bool>(type: "bit", nullable: false),
                    PriceTolerancePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcurementPolicy", x => x.Id);
                    table.CheckConstraint("CK_ProcurementPolicy_Single", "[Id] = 1");
                    table.CheckConstraint("CK_ProcurementPolicy_Tolerance", "[PriceTolerancePercent] >= 0 AND [PriceTolerancePercent] <= 100");
                });

            migrationBuilder.CreateTable(
                name: "Requisition",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('PR-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    NeededBy = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requisition", x => x.Id);
                    table.CheckConstraint("CK_Requisition_Status", "[Status] IN ('Submitted', 'ManagerApproved', 'Approved', 'Ordered', 'Rejected', 'Cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "QuotationRequest",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('RFQ-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    RequisitionId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    QuotationsWanted = table.Column<int>(type: "int", nullable: true),
                    RequestNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RequestedBy = table.Column<int>(type: "int", nullable: false),
                    RecommendedQuotationId = table.Column<int>(type: "int", nullable: true),
                    RecommendationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SubmittedBy = table.Column<int>(type: "int", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecidedBy = table.Column<int>(type: "int", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SelectedQuotationId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotationRequest", x => x.Id);
                    table.CheckConstraint("CK_QuotationRequest_Selected", "[Status] <> 'Selected' OR [SelectedQuotationId] IS NOT NULL");
                    table.CheckConstraint("CK_QuotationRequest_Status", "[Status] IN ('Open', 'Submitted', 'Returned', 'Selected')");
                    table.CheckConstraint("CK_QuotationRequest_Wanted", "[QuotationsWanted] IS NULL OR [QuotationsWanted] BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_QuotationRequest_Requisition_RequisitionId",
                        column: x => x.RequisitionId,
                        principalSchema: "proc",
                        principalTable: "Requisition",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RequisitionEvent",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequisitionId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ActorUserId = table.Column<int>(type: "int", nullable: true),
                    ActorLabel = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    PolicyName = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    At = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequisitionEvent", x => x.Id);
                    table.CheckConstraint("CK_RequisitionEvent_Actor", "[ActorUserId] IS NOT NULL OR ([ActorLabel] IS NOT NULL AND [PolicyName] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_RequisitionEvent_Requisition_RequisitionId",
                        column: x => x.RequisitionId,
                        principalSchema: "proc",
                        principalTable: "Requisition",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RequisitionLine",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequisitionId = table.Column<int>(type: "int", nullable: false),
                    CatalogItemId = table.Column<int>(type: "int", nullable: true),
                    ProjectItemId = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Unit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    EstimatedUnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequisitionLine", x => x.Id);
                    table.CheckConstraint("CK_RequisitionLine_Quantity", "[Quantity] > 0");
                    table.CheckConstraint("CK_RequisitionLine_Unit", "[Unit] IN ('Bag', 'Ton', 'M3', 'M2', 'M', 'Piece', 'Trip', 'Roll', 'Lot', 'Job', 'Day', 'Visit', 'Brick', 'Liter', 'Kg', 'Other')");
                    table.ForeignKey(
                        name: "FK_RequisitionLine_Requisition_RequisitionId",
                        column: x => x.RequisitionId,
                        principalSchema: "proc",
                        principalTable: "Requisition",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Quotation",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: false),
                    SupplierReference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DeliveryDays = table.Column<int>(type: "int", nullable: true),
                    PaymentTerms = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Total = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotation", x => x.Id);
                    table.CheckConstraint("CK_Quotation_DeliveryDays", "[DeliveryDays] IS NULL OR [DeliveryDays] >= 0");
                    table.CheckConstraint("CK_Quotation_Total", "[Total] > 0");
                    table.ForeignKey(
                        name: "FK_Quotation_QuotationRequest_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "proc",
                        principalTable: "QuotationRequest",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrder",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('PO-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    RequisitionId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: false),
                    QuotationId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    ExpectedDelivery = table.Column<DateOnly>(type: "date", nullable: true),
                    PaymentTerms = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelledBy = table.Column<int>(type: "int", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrder", x => x.Id);
                    table.CheckConstraint("CK_PurchaseOrder_Cancelled", "[Status] <> 'Cancelled' OR ([CancelledAt] IS NOT NULL AND [CancelReason] IS NOT NULL)");
                    table.CheckConstraint("CK_PurchaseOrder_Status", "[Status] IN ('Issued', 'PartiallyReceived', 'Received', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_PurchaseOrder_Quotation_QuotationId",
                        column: x => x.QuotationId,
                        principalSchema: "proc",
                        principalTable: "Quotation",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PurchaseOrder_Requisition_RequisitionId",
                        column: x => x.RequisitionId,
                        principalSchema: "proc",
                        principalTable: "Requisition",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "QuotationLine",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QuotationId = table.Column<int>(type: "int", nullable: false),
                    RequisitionLineId = table.Column<int>(type: "int", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotationLine", x => x.Id);
                    table.CheckConstraint("CK_QuotationLine_Price", "[UnitPrice] > 0");
                    table.ForeignKey(
                        name: "FK_QuotationLine_Quotation_QuotationId",
                        column: x => x.QuotationId,
                        principalSchema: "proc",
                        principalTable: "Quotation",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QuotationLine_RequisitionLine_RequisitionLineId",
                        column: x => x.RequisitionLineId,
                        principalSchema: "proc",
                        principalTable: "RequisitionLine",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GoodsReceipt",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('GRN-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    DeliveryNoteNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoodsReceipt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoodsReceipt_PurchaseOrder_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrder",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderLine",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    RequisitionLineId = table.Column<int>(type: "int", nullable: false),
                    CatalogItemId = table.Column<int>(type: "int", nullable: true),
                    ProjectItemId = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Unit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderLine", x => x.Id);
                    table.CheckConstraint("CK_PurchaseOrderLine_Price", "[UnitPrice] > 0");
                    table.CheckConstraint("CK_PurchaseOrderLine_Quantity", "[Quantity] >= 0");
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLine_PurchaseOrder_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrder",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLine_RequisitionLine_RequisitionLineId",
                        column: x => x.RequisitionLineId,
                        principalSchema: "proc",
                        principalTable: "RequisitionLine",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderRevision",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    RevisionNo = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    PreviousLines = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderRevision", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderRevision_PurchaseOrder_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrder",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SupplierInvoice",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('INV-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: false),
                    InvoiceNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DeliveryNoteNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Resolution = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ResolutionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreditNoteNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ResolvedBy = table.Column<int>(type: "int", nullable: true),
                    PostedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    PostedBy = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierInvoice", x => x.Id);
                    table.CheckConstraint("CK_SupplierInvoice_CreditNote", "[Resolution] IS NULL OR [Resolution] <> 'CreditNote' OR [CreditNoteNo] IS NOT NULL");
                    table.CheckConstraint("CK_SupplierInvoice_Posted", "[Status] <> 'Posted' OR [PostedAt] IS NOT NULL");
                    table.CheckConstraint("CK_SupplierInvoice_Resolution", "[Resolution] IS NULL OR [Resolution] IN ('Accept', 'CreditNote', 'ReturnToSupplier')");
                    table.CheckConstraint("CK_SupplierInvoice_Status", "[Status] IN ('Matched', 'Mismatch', 'Posted', 'Returned')");
                    table.CheckConstraint("CK_SupplierInvoice_Total", "[Total] > 0");
                    table.ForeignKey(
                        name: "FK_SupplierInvoice_PurchaseOrder_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrder",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GoodsReceiptLine",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReceiptId = table.Column<int>(type: "int", nullable: false),
                    OrderLineId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoodsReceiptLine", x => x.Id);
                    table.CheckConstraint("CK_GoodsReceiptLine_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_GoodsReceiptLine_GoodsReceipt_ReceiptId",
                        column: x => x.ReceiptId,
                        principalSchema: "proc",
                        principalTable: "GoodsReceipt",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_GoodsReceiptLine_PurchaseOrderLine_OrderLineId",
                        column: x => x.OrderLineId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SupplierInvoiceLine",
                schema: "proc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InvoiceId = table.Column<int>(type: "int", nullable: false),
                    OrderLineId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    OrderUnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PriceMatches = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierInvoiceLine", x => x.Id);
                    table.CheckConstraint("CK_SupplierInvoiceLine_Price", "[UnitPrice] > 0 AND [OrderUnitPrice] > 0");
                    table.CheckConstraint("CK_SupplierInvoiceLine_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_SupplierInvoiceLine_PurchaseOrderLine_OrderLineId",
                        column: x => x.OrderLineId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SupplierInvoiceLine_SupplierInvoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "proc",
                        principalTable: "SupplierInvoice",
                        principalColumn: "Id");
                });

            migrationBuilder.InsertData(
                schema: "proc",
                table: "ProcurementPolicy",
                columns: new[] { "Id", "AutoApproveRequisitions", "PriceTolerancePercent", "UpdatedAt", "UpdatedBy" },
                values: new object[] { 1, true, 0m, null, null });

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipt_OrderId",
                schema: "proc",
                table: "GoodsReceipt",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipt_Project",
                schema: "proc",
                table: "GoodsReceipt",
                columns: new[] { "ProjectId", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_GoodsReceipt_Number",
                schema: "proc",
                table: "GoodsReceipt",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceiptLine_OrderLineId",
                schema: "proc",
                table: "GoodsReceiptLine",
                column: "OrderLineId")
                .Annotation("SqlServer:Include", new[] { "Quantity" });

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceiptLine_ReceiptId",
                schema: "proc",
                table: "GoodsReceiptLine",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_Project",
                schema: "proc",
                table: "PurchaseOrder",
                columns: new[] { "ProjectId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_QuotationId",
                schema: "proc",
                table: "PurchaseOrder",
                column: "QuotationId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_RequisitionId",
                schema: "proc",
                table: "PurchaseOrder",
                column: "RequisitionId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_Supplier",
                schema: "proc",
                table: "PurchaseOrder",
                columns: new[] { "SupplierId", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrder_Number",
                schema: "proc",
                table: "PurchaseOrder",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLine_OrderId",
                schema: "proc",
                table: "PurchaseOrderLine",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLine_RequisitionLineId",
                schema: "proc",
                table: "PurchaseOrderLine",
                column: "RequisitionLineId");

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrderRevision_No",
                schema: "proc",
                table: "PurchaseOrderRevision",
                columns: new[] { "OrderId", "RevisionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotation_RequestId",
                schema: "proc",
                table: "Quotation",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_QuotationLine_RequisitionLineId",
                schema: "proc",
                table: "QuotationLine",
                column: "RequisitionLineId");

            migrationBuilder.CreateIndex(
                name: "UX_QuotationLine_Line",
                schema: "proc",
                table: "QuotationLine",
                columns: new[] { "QuotationId", "RequisitionLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuotationRequest_Project",
                schema: "proc",
                table: "QuotationRequest",
                columns: new[] { "ProjectId", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_QuotationRequest_Number",
                schema: "proc",
                table: "QuotationRequest",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_QuotationRequest_Requisition",
                schema: "proc",
                table: "QuotationRequest",
                column: "RequisitionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Requisition_Project",
                schema: "proc",
                table: "Requisition",
                columns: new[] { "ProjectId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Requisition_Status",
                schema: "proc",
                table: "Requisition",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_Requisition_Number",
                schema: "proc",
                table: "Requisition",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionEvent_RequisitionId",
                schema: "proc",
                table: "RequisitionEvent",
                column: "RequisitionId");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionLine_RequisitionId",
                schema: "proc",
                table: "RequisitionLine",
                column: "RequisitionId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoice_OrderId",
                schema: "proc",
                table: "SupplierInvoice",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoice_Project",
                schema: "proc",
                table: "SupplierInvoice",
                columns: new[] { "ProjectId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoice_Status",
                schema: "proc",
                table: "SupplierInvoice",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_SupplierInvoice_Number",
                schema: "proc",
                table: "SupplierInvoice",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_SupplierInvoice_Supplier_Number",
                schema: "proc",
                table: "SupplierInvoice",
                columns: new[] { "SupplierId", "InvoiceNo" },
                unique: true,
                filter: "[Status] <> 'Returned'");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoiceLine_InvoiceId",
                schema: "proc",
                table: "SupplierInvoiceLine",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoiceLine_OrderLineId",
                schema: "proc",
                table: "SupplierInvoiceLine",
                column: "OrderLineId")
                .Annotation("SqlServer:Include", new[] { "InvoiceId", "Quantity" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoodsReceiptLine",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "ProcurementPolicy",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "PurchaseOrderRevision",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "QuotationLine",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "RequisitionEvent",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "SupplierInvoiceLine",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "GoodsReceipt",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "PurchaseOrderLine",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "SupplierInvoice",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "RequisitionLine",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "PurchaseOrder",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "Quotation",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "QuotationRequest",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "Requisition",
                schema: "proc");
        }
    }
}
