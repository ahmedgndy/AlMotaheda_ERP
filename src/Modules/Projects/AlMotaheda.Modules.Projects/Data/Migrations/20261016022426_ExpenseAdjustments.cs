using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <inheritdoc />
    internal partial class ExpenseAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExpenseAdjustment",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExpenseId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RequestedBy = table.Column<int>(type: "int", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecidedBy = table.Column<int>(type: "int", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseAdjustment", x => x.Id);
                    table.CheckConstraint("CK_ExpenseAdjustment_Amount", "[Amount] <> 0");
                    table.CheckConstraint("CK_ExpenseAdjustment_Status", "[Status] IN ('Requested', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_ExpenseAdjustment_ProjectExpense_ExpenseId",
                        column: x => x.ExpenseId,
                        principalSchema: "projects",
                        principalTable: "ProjectExpense",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseAdjustment_Expense_Status",
                schema: "projects",
                table: "ExpenseAdjustment",
                columns: new[] { "ExpenseId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_ExpenseAdjustment_OneOpen",
                schema: "projects",
                table: "ExpenseAdjustment",
                column: "ExpenseId",
                unique: true,
                filter: "[Status] = 'Requested'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpenseAdjustment",
                schema: "projects");
        }
    }
}
