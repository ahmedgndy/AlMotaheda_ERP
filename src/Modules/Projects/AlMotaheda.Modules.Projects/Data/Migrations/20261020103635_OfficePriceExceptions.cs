using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <inheritdoc />
    internal partial class OfficePriceExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Office expenses (company overhead) carry price exceptions with no project. SQL Server can't change the
            // nullability of a column an index or a foreign key depends on, so those are dropped and rebuilt around it.
            // No row changes: every existing case keeps its project.
            migrationBuilder.DropForeignKey(name: "FK_PriceException_Project_ProjectId", schema: "projects", table: "PriceException");
            migrationBuilder.DropIndex(name: "IX_PriceException_Project_Status", schema: "projects", table: "PriceException");
            migrationBuilder.DropIndex(name: "IX_PriceExceptionAlert_Project_CreatedAt", schema: "projects", table: "PriceExceptionAlert");
            migrationBuilder.DropCheckConstraint(name: "CK_PriceException_SourceType", schema: "projects", table: "PriceException");

            migrationBuilder.AlterColumn<int>(
                name: "ProjectId", schema: "projects", table: "PriceExceptionAlert", type: "int", nullable: true, oldClrType: typeof(int), oldType: "int");
            migrationBuilder.AlterColumn<int>(
                name: "ProjectId", schema: "projects", table: "PriceException", type: "int", nullable: true, oldClrType: typeof(int), oldType: "int");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PriceException_SourceType", schema: "projects", table: "PriceException",
                sql: "[SourceType] IN ('ProjectExpense', 'SupplierInvoiceLine', 'OfficeExpense')");
            migrationBuilder.CreateIndex(name: "IX_PriceException_Project_Status", schema: "projects", table: "PriceException", columns: new[] { "ProjectId", "Status" });
            migrationBuilder.CreateIndex(name: "IX_PriceExceptionAlert_Project_CreatedAt", schema: "projects", table: "PriceExceptionAlert", columns: new[] { "ProjectId", "CreatedAt" });
            migrationBuilder.AddForeignKey(
                name: "FK_PriceException_Project_ProjectId", schema: "projects", table: "PriceException", column: "ProjectId",
                principalSchema: "projects", principalTable: "Project", principalColumn: "Id", onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only possible while no office case exists (a case without a project can't get one back).
            migrationBuilder.DropForeignKey(name: "FK_PriceException_Project_ProjectId", schema: "projects", table: "PriceException");
            migrationBuilder.DropIndex(name: "IX_PriceException_Project_Status", schema: "projects", table: "PriceException");
            migrationBuilder.DropIndex(name: "IX_PriceExceptionAlert_Project_CreatedAt", schema: "projects", table: "PriceExceptionAlert");
            migrationBuilder.DropCheckConstraint(name: "CK_PriceException_SourceType", schema: "projects", table: "PriceException");

            migrationBuilder.AlterColumn<int>(
                name: "ProjectId", schema: "projects", table: "PriceExceptionAlert", type: "int", nullable: false, oldClrType: typeof(int), oldType: "int", oldNullable: true);
            migrationBuilder.AlterColumn<int>(
                name: "ProjectId", schema: "projects", table: "PriceException", type: "int", nullable: false, oldClrType: typeof(int), oldType: "int", oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PriceException_SourceType", schema: "projects", table: "PriceException",
                sql: "[SourceType] IN ('ProjectExpense', 'SupplierInvoiceLine')");
            migrationBuilder.CreateIndex(name: "IX_PriceException_Project_Status", schema: "projects", table: "PriceException", columns: new[] { "ProjectId", "Status" });
            migrationBuilder.CreateIndex(name: "IX_PriceExceptionAlert_Project_CreatedAt", schema: "projects", table: "PriceExceptionAlert", columns: new[] { "ProjectId", "CreatedAt" });
            migrationBuilder.AddForeignKey(
                name: "FK_PriceException_Project_ProjectId", schema: "projects", table: "PriceException", column: "ProjectId",
                principalSchema: "projects", principalTable: "Project", principalColumn: "Id", onDelete: ReferentialAction.NoAction);
        }
    }
}
