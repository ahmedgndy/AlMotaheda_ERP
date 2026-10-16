using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <inheritdoc />
    internal partial class ExpenseSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceId",
                schema: "projects",
                table: "ProjectExpense",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                schema: "projects",
                table: "ProjectExpense",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_ProjectExpense_Source",
                schema: "projects",
                table: "ProjectExpense",
                columns: new[] { "SourceType", "SourceId" },
                unique: true,
                filter: "[SourceId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProjectExpense_Source",
                schema: "projects",
                table: "ProjectExpense",
                sql: "([SourceType] IS NULL AND [SourceId] IS NULL) OR ([SourceType] IS NOT NULL AND [SourceId] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_ProjectExpense_Source",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProjectExpense_Source",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropColumn(
                name: "SourceId",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropColumn(
                name: "SourceType",
                schema: "projects",
                table: "ProjectExpense");
        }
    }
}
