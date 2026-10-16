using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <inheritdoc />
    internal partial class SupplierUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A unique index can't be created over duplicates. Stop with a clear message instead of a raw SQL error, so
            // the duplicates are merged first (the same supplier entered twice splits its balance). Nothing changes here.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [projects].[Supplier] GROUP BY [NameAr] HAVING COUNT(*) > 1)
                   OR EXISTS (SELECT 1 FROM [projects].[Supplier] WHERE [TaxNumber] IS NOT NULL GROUP BY [TaxNumber] HAVING COUNT(*) > 1)
                    THROW 50001, 'Duplicate suppliers: two or more share an Arabic name or a tax number. Merge them first (SELECT [NameAr], [TaxNumber], COUNT(*) FROM [projects].[Supplier] GROUP BY ...), then apply this migration.', 1;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_Supplier_NameAr",
                schema: "projects",
                table: "Supplier",
                column: "NameAr",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Supplier_TaxNumber",
                schema: "projects",
                table: "Supplier",
                column: "TaxNumber",
                unique: true,
                filter: "[TaxNumber] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Supplier_NameAr",
                schema: "projects",
                table: "Supplier");

            migrationBuilder.DropIndex(
                name: "UX_Supplier_TaxNumber",
                schema: "projects",
                table: "Supplier");
        }
    }
}
