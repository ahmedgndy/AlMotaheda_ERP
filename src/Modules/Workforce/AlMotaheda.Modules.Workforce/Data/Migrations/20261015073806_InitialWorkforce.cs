using System;
using AlMotaheda.Modules.Workforce.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AlMotaheda.Modules.Workforce.Data.Migrations
{
    /// <inheritdoc />
    internal partial class InitialWorkforce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "workforce");

            migrationBuilder.CreateTable(
                name: "DailySheet",
                schema: "workforce",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Shift = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    RecordedBy = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailySheet", x => x.Id);
                    table.CheckConstraint("CK_DailySheet_Review", "[Status] IN ('Draft', 'Submitted') OR ([ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_DailySheet_Shift", "[Shift] IN ('Day', 'Night')");
                    table.CheckConstraint("CK_DailySheet_Status", "[Status] IN ('Draft', 'Submitted', 'Returned', 'Approved', 'Rejected')");
                    table.CheckConstraint("CK_DailySheet_Submitted", "[Status] = 'Draft' OR [SubmittedAt] IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "Trade",
                schema: "workforce",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DefaultDailyRate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DefaultWorkType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trade", x => x.Id);
                    table.CheckConstraint("CK_Trade_DefaultDailyRate", "[DefaultDailyRate] > 0");
                    table.CheckConstraint("CK_Trade_DefaultWorkType", "[DefaultWorkType] IN ('Concrete', 'SteelFixing', 'Formwork', 'Brickwork', 'Plastering', 'Tiling', 'Painting', 'Excavation', 'Waterproofing', 'Piling', 'Mep', 'Guarding', 'EquipmentOperation', 'General')");
                });

            migrationBuilder.CreateTable(
                name: "Worker",
                schema: "workforce",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NationalId = table.Column<string>(type: "varchar(14)", unicode: false, maxLength: 14, nullable: true),
                    Phone = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    TradeId = table.Column<int>(type: "int", nullable: false),
                    DailyRate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
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
                    table.PrimaryKey("PK_Worker", x => x.Id);
                    table.CheckConstraint("CK_Worker_DailyRate", "[DailyRate] IS NULL OR [DailyRate] > 0");
                    table.CheckConstraint("CK_Worker_NationalId", "[NationalId] IS NULL OR (LEN([NationalId]) = 14 AND [NationalId] NOT LIKE '%[^0-9]%')");
                    table.CheckConstraint("CK_Worker_Status", "[Status] IN ('Active', 'Inactive')");
                    table.ForeignKey(
                        name: "FK_Worker_Trade_TradeId",
                        column: x => x.TradeId,
                        principalSchema: "workforce",
                        principalTable: "Trade",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AttendanceLine",
                schema: "workforce",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SheetId = table.Column<int>(type: "int", nullable: false),
                    WorkerId = table.Column<int>(type: "int", nullable: false),
                    TradeId = table.Column<int>(type: "int", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Shift = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Days = table.Column<decimal>(type: "decimal(2,1)", precision: 2, scale: 1, nullable: false),
                    OvertimeHours = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: false),
                    WorkType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IsVoid = table.Column<bool>(type: "bit", nullable: false),
                    AppliedDailyRate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    BaseAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    OvertimeAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    AllowancesAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    DeductionsAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    NetAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceLine", x => x.Id);
                    table.CheckConstraint("CK_AttendanceLine_Days", "[Days] IN (0.5, 1)");
                    table.CheckConstraint("CK_AttendanceLine_OvertimeHours", "[OvertimeHours] >= 0 AND [OvertimeHours] <= 12");
                    table.CheckConstraint("CK_AttendanceLine_Pay", "([AppliedDailyRate] IS NULL AND [BaseAmount] IS NULL AND [OvertimeAmount] IS NULL AND [AllowancesAmount] IS NULL AND [DeductionsAmount] IS NULL AND [NetAmount] IS NULL) OR ([AppliedDailyRate] IS NOT NULL AND [BaseAmount] IS NOT NULL AND [OvertimeAmount] IS NOT NULL AND [AllowancesAmount] IS NOT NULL AND [DeductionsAmount] IS NOT NULL AND [NetAmount] IS NOT NULL AND [AppliedDailyRate] > 0 AND [BaseAmount] >= 0 AND [OvertimeAmount] >= 0 AND [AllowancesAmount] >= 0 AND [DeductionsAmount] >= 0 AND [NetAmount] >= 0 AND [NetAmount] = [BaseAmount] + [OvertimeAmount] + [AllowancesAmount] - [DeductionsAmount])");
                    table.CheckConstraint("CK_AttendanceLine_Shift", "[Shift] IN ('Day', 'Night')");
                    table.CheckConstraint("CK_AttendanceLine_WorkType", "[WorkType] IN ('Concrete', 'SteelFixing', 'Formwork', 'Brickwork', 'Plastering', 'Tiling', 'Painting', 'Excavation', 'Waterproofing', 'Piling', 'Mep', 'Guarding', 'EquipmentOperation', 'General')");
                    table.ForeignKey(
                        name: "FK_AttendanceLine_DailySheet_SheetId",
                        column: x => x.SheetId,
                        principalSchema: "workforce",
                        principalTable: "DailySheet",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AttendanceLine_Trade_TradeId",
                        column: x => x.TradeId,
                        principalSchema: "workforce",
                        principalTable: "Trade",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AttendanceLine_Worker_WorkerId",
                        column: x => x.WorkerId,
                        principalSchema: "workforce",
                        principalTable: "Worker",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "WorkerAssignment",
                schema: "workforce",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkerId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    DailyRate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    AssignedBy = table.Column<int>(type: "int", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    EndedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerAssignment", x => x.Id);
                    table.CheckConstraint("CK_WorkerAssignment_DailyRate", "[DailyRate] IS NULL OR [DailyRate] > 0");
                    table.CheckConstraint("CK_WorkerAssignment_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
                    table.CheckConstraint("CK_WorkerAssignment_Ending", "([EndDate] IS NULL AND [EndedAt] IS NULL AND [EndedBy] IS NULL) OR ([EndDate] IS NOT NULL AND [EndedAt] IS NOT NULL AND [EndedBy] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WorkerAssignment_Worker_WorkerId",
                        column: x => x.WorkerId,
                        principalSchema: "workforce",
                        principalTable: "Worker",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "WorkerPayment",
                schema: "workforce",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkerId = table.Column<int>(type: "int", nullable: false),
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
                    table.PrimaryKey("PK_WorkerPayment", x => x.Id);
                    table.CheckConstraint("CK_WorkerPayment_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_WorkerPayment_Method", "[Method] IN ('Cash', 'BankTransfer')");
                    table.CheckConstraint("CK_WorkerPayment_Void", "([VoidedAt] IS NULL AND [VoidedBy] IS NULL AND [VoidReason] IS NULL) OR ([VoidedAt] IS NOT NULL AND [VoidedBy] IS NOT NULL AND [VoidReason] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WorkerPayment_Worker_WorkerId",
                        column: x => x.WorkerId,
                        principalSchema: "workforce",
                        principalTable: "Worker",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "LaborAllocation",
                schema: "workforce",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LineId = table.Column<int>(type: "int", nullable: false),
                    ProjectItemId = table.Column<int>(type: "int", nullable: false),
                    Portion = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaborAllocation", x => x.Id);
                    table.CheckConstraint("CK_LaborAllocation_Amount", "[Amount] IS NULL OR [Amount] >= 0");
                    table.CheckConstraint("CK_LaborAllocation_Portion", "[Portion] > 0 AND [Portion] <= 1");
                    table.ForeignKey(
                        name: "FK_LaborAllocation_AttendanceLine_LineId",
                        column: x => x.LineId,
                        principalSchema: "workforce",
                        principalTable: "AttendanceLine",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "LineAdjustment",
                schema: "workforce",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LineId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Type = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineAdjustment", x => x.Id);
                    table.CheckConstraint("CK_LineAdjustment_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_LineAdjustment_DeductionReason", "[Kind] = 'Allowance' OR [Reason] IS NOT NULL");
                    table.CheckConstraint("CK_LineAdjustment_Kind", "[Kind] IN ('Allowance', 'Deduction')");
                    table.CheckConstraint("CK_LineAdjustment_Type", "[Type] IN ('Food', 'Transport', 'Night', 'Accommodation', 'Tip', 'Other')");
                    table.ForeignKey(
                        name: "FK_LineAdjustment_AttendanceLine_LineId",
                        column: x => x.LineId,
                        principalSchema: "workforce",
                        principalTable: "AttendanceLine",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "WorkerPaymentAllocation",
                schema: "workforce",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PaymentId = table.Column<int>(type: "int", nullable: false),
                    LineId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerPaymentAllocation", x => x.Id);
                    table.CheckConstraint("CK_WorkerPaymentAllocation_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_WorkerPaymentAllocation_AttendanceLine_LineId",
                        column: x => x.LineId,
                        principalSchema: "workforce",
                        principalTable: "AttendanceLine",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WorkerPaymentAllocation_WorkerPayment_PaymentId",
                        column: x => x.PaymentId,
                        principalSchema: "workforce",
                        principalTable: "WorkerPayment",
                        principalColumn: "Id");
                });

            migrationBuilder.InsertData(
                schema: "workforce",
                table: "Trade",
                columns: new[] { "Id", "Code", "DefaultDailyRate", "DefaultWorkType", "IsActive", "NameAr", "NameEn" },
                values: new object[,]
                {
                    { 1, "construction", 300m, "General", true, "عامل", "Labourer" },
                    { 2, "general", 250m, "General", true, "مساعد", "Helper" },
                    { 3, "mason", 480m, "Brickwork", true, "بنّاء", "Mason" },
                    { 4, "steel", 600m, "SteelFixing", true, "حدّاد مسلح", "Steel fixer" },
                    { 5, "carpenter", 550m, "Formwork", true, "نجّار مسلح", "Formwork carpenter" },
                    { 6, "tile", 530m, "Tiling", true, "مبلّط", "Tiler" },
                    { 7, "painter", 430m, "Painting", true, "نقّاش", "Painter" },
                    { 8, "excavation", 350m, "Excavation", true, "عامل حفر", "Excavation worker" },
                    { 9, "electrician", 580m, "Mep", true, "كهربائي", "Electrician" },
                    { 10, "plumber", 550m, "Mep", true, "سبّاك", "Plumber" },
                    { 11, "waterproofing", 600m, "Waterproofing", true, "فني عزل", "Waterproofing technician" },
                    { 12, "piles", 530m, "Piling", true, "عامل خوازيق", "Piles worker" },
                    { 13, "security", 450m, "Guarding", true, "غفير", "Guard" },
                    { 14, "plasterer", 480m, "Plastering", true, "مبيّض محارة", "Plasterer" },
                    { 15, "operator", 650m, "EquipmentOperation", true, "مشغّل معدات", "Equipment operator" },
                    { 16, "other", 550m, "General", true, "أخرى", "Other" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceLine_SheetId",
                schema: "workforce",
                table: "AttendanceLine",
                column: "SheetId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceLine_TradeId",
                schema: "workforce",
                table: "AttendanceLine",
                column: "TradeId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceLine_Worker_Date",
                schema: "workforce",
                table: "AttendanceLine",
                columns: new[] { "WorkerId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "UX_AttendanceLine_Worker_Shift",
                schema: "workforce",
                table: "AttendanceLine",
                columns: new[] { "WorkerId", "WorkDate", "Shift" },
                unique: true,
                filter: "[IsVoid] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_DailySheet_Project_Date",
                schema: "workforce",
                table: "DailySheet",
                columns: new[] { "ProjectId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_DailySheet_Project_Status",
                schema: "workforce",
                table: "DailySheet",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_DailySheet_Recorder_Day",
                schema: "workforce",
                table: "DailySheet",
                columns: new[] { "ProjectId", "WorkDate", "Shift", "RecordedBy" },
                unique: true,
                filter: "[Status] <> 'Rejected'");

            migrationBuilder.CreateIndex(
                name: "IX_LaborAllocation_ProjectItemId",
                schema: "workforce",
                table: "LaborAllocation",
                column: "ProjectItemId");

            migrationBuilder.CreateIndex(
                name: "UX_LaborAllocation_Line_Item",
                schema: "workforce",
                table: "LaborAllocation",
                columns: new[] { "LineId", "ProjectItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LineAdjustment_LineId",
                schema: "workforce",
                table: "LineAdjustment",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "UX_Trade_Code",
                schema: "workforce",
                table: "Trade",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Worker_NameEn",
                schema: "workforce",
                table: "Worker",
                column: "NameEn");

            migrationBuilder.CreateIndex(
                name: "IX_Worker_TradeId",
                schema: "workforce",
                table: "Worker",
                column: "TradeId");

            migrationBuilder.CreateIndex(
                name: "UX_Worker_NationalId",
                schema: "workforce",
                table: "Worker",
                column: "NationalId",
                unique: true,
                filter: "[NationalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerAssignment_ProjectId",
                schema: "workforce",
                table: "WorkerAssignment",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerAssignment_Worker_Project",
                schema: "workforce",
                table: "WorkerAssignment",
                columns: new[] { "WorkerId", "ProjectId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkerAssignment_Open",
                schema: "workforce",
                table: "WorkerAssignment",
                columns: new[] { "WorkerId", "ProjectId" },
                unique: true,
                filter: "[EndDate] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerPayment_Worker_Date",
                schema: "workforce",
                table: "WorkerPayment",
                columns: new[] { "WorkerId", "PaymentDate" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkerPayment_IdempotencyKey",
                schema: "workforce",
                table: "WorkerPayment",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkerPaymentAllocation_LineId",
                schema: "workforce",
                table: "WorkerPaymentAllocation",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "UX_WorkerPaymentAllocation_Payment_Line",
                schema: "workforce",
                table: "WorkerPaymentAllocation",
                columns: new[] { "PaymentId", "LineId" },
                unique: true);

            // Written by hand: projects.Project and projects.ProjectItem belong to the Projects module, so they aren't
            // in this module's model. Projects and items are never deleted, so the keys never block; they stop labor
            // for a project or an item that doesn't exist. The Projects migrations must be applied before this one.
            migrationBuilder.AddForeignKey(
                name: WorkerAssignmentConfiguration.ProjectForeignKey,
                schema: "workforce",
                table: "WorkerAssignment",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: DailySheetConfiguration.ProjectForeignKey,
                schema: "workforce",
                table: "DailySheet",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: LaborAllocationConfiguration.ProjectItemForeignKey,
                schema: "workforce",
                table: "LaborAllocation",
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
                name: LaborAllocationConfiguration.ProjectItemForeignKey,
                schema: "workforce",
                table: "LaborAllocation");

            migrationBuilder.DropForeignKey(
                name: DailySheetConfiguration.ProjectForeignKey,
                schema: "workforce",
                table: "DailySheet");

            migrationBuilder.DropForeignKey(
                name: WorkerAssignmentConfiguration.ProjectForeignKey,
                schema: "workforce",
                table: "WorkerAssignment");

            migrationBuilder.DropTable(
                name: "LaborAllocation",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "LineAdjustment",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "WorkerAssignment",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "WorkerPaymentAllocation",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "AttendanceLine",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "WorkerPayment",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "DailySheet",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "Worker",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "Trade",
                schema: "workforce");
        }
    }
}
