using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Finance.Data.Migrations
{
    /// <summary>
    /// Copies the payments recorded before Finance existed into payment requests and the ledger
    /// (<see cref="LegacyPaymentImport"/>). Data only: no table changes.
    /// </summary>
    internal partial class ImportExistingPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LegacyPaymentImport.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LegacyPaymentImport.UndoSql);
        }
    }
}
