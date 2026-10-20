using System;
using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <summary>
    /// Central Price Control (docs/project-workspace/10-price-control.md): the configurable price categories and
    /// units (seeded with every classification and unit the price list used, same codes, plus meals, fuel, internet,
    /// office, maintenance, transport, equipment rental, worker/day, meal, month, subscription, hour, km); the price
    /// item's classification becomes a category (every item is mapped by code; an unknown code falls to Other); a
    /// range carries its rule kind (Range, Fixed, LumpSum), reference price and tolerance; the price exception cases
    /// and their alert rows. No price, range or record is changed or removed.
    /// </summary>
    /// <remarks>
    /// Down writes the classification back from the category code; it fails if an item uses one of the new
    /// categories (not in the old check), and drops the cases, categories and units.
    /// </remarks>
    internal partial class PriceControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. The configurable lookups, seeded with every classification and unit the price list knew (same codes).
            migrationBuilder.CreateTable(
                name: "PriceCategory",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceCategory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_PriceCategory_Code",
                schema: "projects",
                table: "PriceCategory",
                column: "Code",
                unique: true);

            migrationBuilder.CreateTable(
                name: "PriceUnit",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceUnit", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_PriceUnit_Code",
                schema: "projects",
                table: "PriceUnit",
                column: "Code",
                unique: true);

            migrationBuilder.InsertData(
                schema: "projects",
                table: "PriceCategory",
                columns: new[] { "Id", "Code", "IsActive", "NameAr", "NameEn", "SortOrder", "UpdatedAt", "UpdatedBy" },
                values: new object[,]
                {
                    { 1, "CementAggregates", true, "أسمنت وركام", "Cement and aggregates", 1, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 2, "ReadyMix", true, "خرسانة جاهزة", "Ready-mix concrete", 2, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 3, "Steel", true, "حديد", "Steel", 3, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 4, "Masonry", true, "مباني", "Masonry", 4, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 5, "Finishing", true, "تشطيبات", "Finishing", 5, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 6, "Waterproofing", true, "عزل", "Waterproofing", 6, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 7, "Piling", true, "خوازيق", "Piling", 7, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 8, "Formwork", true, "شدات", "Formwork", 8, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 9, "Consumables", true, "مستهلكات", "Consumables", 9, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 10, "Services", true, "خدمات", "Services", 10, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 11, "Other", true, "أخرى", "Other", 11, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 12, "Meals", true, "وجبات العمال", "Worker meals and food", 12, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 13, "Fuel", true, "وقود", "Fuel", 13, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 14, "Internet", true, "اشتراكات الإنترنت", "Internet subscriptions", 14, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 15, "OfficeExpenses", true, "مصروفات مكتبية", "Office expenses", 15, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 16, "Maintenance", true, "صيانة", "Maintenance", 16, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 17, "Transport", true, "نقل", "Transport", 17, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 18, "EquipmentRental", true, "تأجير معدات", "Equipment rental", 18, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null }
                });

            migrationBuilder.InsertData(
                schema: "projects",
                table: "PriceUnit",
                columns: new[] { "Id", "Code", "IsActive", "NameAr", "NameEn", "SortOrder", "UpdatedAt", "UpdatedBy" },
                values: new object[,]
                {
                    { 1, "Bag", true, "شيكارة", "Bag", 1, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 2, "Ton", true, "طن", "Ton", 2, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 3, "M3", true, "م³", "Cubic meter", 3, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 4, "M2", true, "م²", "Square meter", 4, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 5, "M", true, "متر", "Meter", 5, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 6, "Piece", true, "قطعة", "Piece", 6, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 7, "Trip", true, "نقلة", "Trip", 7, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 8, "Roll", true, "لفة", "Roll", 8, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 9, "Lot", true, "مقطوعية", "Lot", 9, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 10, "Job", true, "عملية", "Job", 10, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 11, "Day", true, "يوم", "Day", 11, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 12, "Visit", true, "زيارة", "Visit", 12, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 13, "Brick", true, "طوبة", "Brick", 13, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 14, "Liter", true, "لتر", "Liter", 14, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 15, "Kg", true, "كجم", "Kilogram", 15, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 16, "Other", true, "أخرى", "Other", 16, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 17, "WorkerDay", true, "عامل/يوم", "Worker per day", 17, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 18, "Meal", true, "وجبة", "Meal", 18, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 19, "Month", true, "شهر", "Month", 19, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 20, "Subscription", true, "اشتراك شهري", "Monthly subscription", 20, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 21, "Hour", true, "ساعة", "Hour", 21, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 22, "Km", true, "كم", "Kilometer", 22, new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), null }
                });

            // 2. Every price item takes the category whose code is its old classification; the old column goes.
            migrationBuilder.AddColumn<int>(
                name: "CategoryId",
                schema: "projects",
                table: "PriceItem",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE item SET item.CategoryId = category.Id
                FROM [projects].[PriceItem] item
                JOIN [projects].[PriceCategory] category ON category.Code = item.Classification;
                UPDATE [projects].[PriceItem] SET CategoryId = (SELECT Id FROM [projects].[PriceCategory] WHERE Code = 'Other') WHERE CategoryId = 0;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_PriceItem_Classification",
                schema: "projects",
                table: "PriceItem");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PriceItem_Unit",
                schema: "projects",
                table: "PriceItem");

            migrationBuilder.DropColumn(
                name: "Classification",
                schema: "projects",
                table: "PriceItem");

            // 3. The rule kind, reference price and tolerance of a range (every existing range is a plain min/max range).
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "projects",
                table: "PriceRange",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: false,
                defaultValue: "Range");

            migrationBuilder.AddColumn<decimal>(
                name: "ReferencePrice",
                schema: "projects",
                table: "PriceRange",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceNote",
                schema: "projects",
                table: "PriceRange",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TolerancePercent",
                schema: "projects",
                table: "PriceRange",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PriceException",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    SourceId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: true),
                    PriceItemId = table.Column<int>(type: "int", nullable: false),
                    PriceRangeId = table.Column<int>(type: "int", nullable: true),
                    Unit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ReferenceMinPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ReferenceMaxPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    VariancePerUnit = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    VarianceTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    VariancePercent = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    TransactionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SubmittedBy = table.Column<int>(type: "int", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecidedBy = table.Column<int>(type: "int", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    NewRangeId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceException", x => x.Id);
                    table.CheckConstraint("CK_PriceException_Amounts", "[Quantity] > 0 AND [UnitPrice] > 0 AND [VariancePerUnit] >= 0 AND [VarianceTotal] >= 0");
                    table.CheckConstraint("CK_PriceException_Decision", "([Status] = 'Pending' AND [DecidedAt] IS NULL AND [DecidedBy] IS NULL) OR ([Status] <> 'Pending' AND [DecidedAt] IS NOT NULL AND [DecidedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_PriceException_NewRange", "[NewRangeId] IS NULL OR [Status] = 'ApprovedWithPriceChange'");
                    table.CheckConstraint("CK_PriceException_SourceType", "[SourceType] IN ('ProjectExpense', 'SupplierInvoiceLine')");
                    table.CheckConstraint("CK_PriceException_Status", "[Status] IN ('Pending', 'ApprovedOnce', 'ApprovedWithPriceChange', 'Rejected', 'Withdrawn')");
                    table.ForeignKey(
                        name: "FK_PriceException_PriceItem_PriceItemId",
                        column: x => x.PriceItemId,
                        principalSchema: "projects",
                        principalTable: "PriceItem",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PriceException_PriceRange_NewRangeId",
                        column: x => x.NewRangeId,
                        principalSchema: "projects",
                        principalTable: "PriceRange",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PriceException_PriceRange_PriceRangeId",
                        column: x => x.PriceRangeId,
                        principalSchema: "projects",
                        principalTable: "PriceRange",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PriceException_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PriceExceptionAlert",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExceptionId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    RecipientUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceExceptionAlert", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceExceptionAlert_PriceException_ExceptionId",
                        column: x => x.ExceptionId,
                        principalSchema: "projects",
                        principalTable: "PriceException",
                        principalColumn: "Id");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PriceRange_Kind",
                schema: "projects",
                table: "PriceRange",
                sql: "[Kind] IN ('Range', 'Fixed', 'LumpSum')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PriceRange_Reference",
                schema: "projects",
                table: "PriceRange",
                sql: "[ReferencePrice] IS NULL OR ([ReferencePrice] >= [MinPrice] AND [ReferencePrice] <= [MaxPrice])");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PriceRange_Tolerance",
                schema: "projects",
                table: "PriceRange",
                sql: "[TolerancePercent] IS NULL OR ([TolerancePercent] >= 0 AND [TolerancePercent] <= 100)");

            migrationBuilder.CreateIndex(
                name: "IX_PriceItem_CategoryId",
                schema: "projects",
                table: "PriceItem",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceException_NewRangeId",
                schema: "projects",
                table: "PriceException",
                column: "NewRangeId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceException_PriceItem_Status",
                schema: "projects",
                table: "PriceException",
                columns: new[] { "PriceItemId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceException_PriceRangeId",
                schema: "projects",
                table: "PriceException",
                column: "PriceRangeId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceException_Project_Status",
                schema: "projects",
                table: "PriceException",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceException_Status_SubmittedAt",
                schema: "projects",
                table: "PriceException",
                columns: new[] { "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceException_SubmittedBy",
                schema: "projects",
                table: "PriceException",
                column: "SubmittedBy");

            migrationBuilder.CreateIndex(
                name: "UX_PriceException_Source_Pending",
                schema: "projects",
                table: "PriceException",
                columns: new[] { "SourceType", "SourceId" },
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_PriceExceptionAlert_Project_CreatedAt",
                schema: "projects",
                table: "PriceExceptionAlert",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_PriceExceptionAlert_Exception_Recipient",
                schema: "projects",
                table: "PriceExceptionAlert",
                columns: new[] { "ExceptionId", "RecipientUserId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PriceItem_PriceCategory_CategoryId",
                schema: "projects",
                table: "PriceItem",
                column: "CategoryId",
                principalSchema: "projects",
                principalTable: "PriceCategory",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PriceException_Supplier_SupplierId",
                schema: "projects",
                table: "PriceException",
                column: "SupplierId",
                principalSchema: "projects",
                principalTable: "Supplier",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            // The people are Access records: these keys are added by hand (Access owns the table).
            migrationBuilder.AddForeignKey(
                name: "FK_PriceException_User_SubmittedBy",
                schema: "projects",
                table: "PriceException",
                column: "SubmittedBy",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: PriceExceptionAlertConfiguration.RecipientForeignKey,
                schema: "projects",
                table: "PriceExceptionAlert",
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
                name: "FK_PriceItem_PriceCategory_CategoryId",
                schema: "projects",
                table: "PriceItem");

            migrationBuilder.DropTable(
                name: "PriceExceptionAlert",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "PriceUnit",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "PriceException",
                schema: "projects");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PriceRange_Kind",
                schema: "projects",
                table: "PriceRange");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PriceRange_Reference",
                schema: "projects",
                table: "PriceRange");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PriceRange_Tolerance",
                schema: "projects",
                table: "PriceRange");

            migrationBuilder.DropIndex(
                name: "IX_PriceItem_CategoryId",
                schema: "projects",
                table: "PriceItem");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "projects",
                table: "PriceRange");

            migrationBuilder.DropColumn(
                name: "ReferencePrice",
                schema: "projects",
                table: "PriceRange");

            migrationBuilder.DropColumn(
                name: "SourceNote",
                schema: "projects",
                table: "PriceRange");

            migrationBuilder.DropColumn(
                name: "TolerancePercent",
                schema: "projects",
                table: "PriceRange");

            migrationBuilder.AddColumn<string>(
                name: "Classification",
                schema: "projects",
                table: "PriceItem",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                """
                UPDATE item SET item.Classification = category.Code
                FROM [projects].[PriceItem] item
                JOIN [projects].[PriceCategory] category ON category.Id = item.CategoryId;
                """);

            migrationBuilder.DropTable(
                name: "PriceCategory",
                schema: "projects");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                schema: "projects",
                table: "PriceItem");


            migrationBuilder.AddCheckConstraint(
                name: "CK_PriceItem_Classification",
                schema: "projects",
                table: "PriceItem",
                sql: "[Classification] IN ('CementAggregates', 'ReadyMix', 'Steel', 'Masonry', 'Finishing', 'Waterproofing', 'Piling', 'Formwork', 'Consumables', 'Services', 'Other')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PriceItem_Unit",
                schema: "projects",
                table: "PriceItem",
                sql: "[Unit] IN ('Bag', 'Ton', 'M3', 'M2', 'M', 'Piece', 'Trip', 'Roll', 'Lot', 'Job', 'Day', 'Visit', 'Brick', 'Liter', 'Kg', 'Other')");
        }
    }
}
