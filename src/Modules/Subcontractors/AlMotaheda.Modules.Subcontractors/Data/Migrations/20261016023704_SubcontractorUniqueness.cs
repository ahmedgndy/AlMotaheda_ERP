using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Subcontractors.Data.Migrations
{
    /// <inheritdoc />
    internal partial class SubcontractorUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A unique index can't be created over duplicates. Stop with a clear message instead of a raw SQL error, so
            // the duplicates are merged first. Nothing changes here.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [sub].[Subcontractor] GROUP BY [NameAr] HAVING COUNT(*) > 1)
                    THROW 50001, 'Duplicate subcontractors: two or more share an Arabic name. Merge them first (SELECT [NameAr], COUNT(*) FROM [sub].[Subcontractor] GROUP BY [NameAr] HAVING COUNT(*) > 1), then apply this migration.', 1;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_Subcontractor_NameAr",
                schema: "sub",
                table: "Subcontractor",
                column: "NameAr",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Subcontractor_NameAr",
                schema: "sub",
                table: "Subcontractor");
        }
    }
}
