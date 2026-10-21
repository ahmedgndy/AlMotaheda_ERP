using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Finance.Data.Migrations
{
    /// <summary>
    /// Section 16.A: a custody adjustment carries the holder it was posted against, a shortfall when a debit went
    /// beyond the balance, and a status with the holder's dispute and the reviewer's resolution (a reversal is an
    /// opposite adjustment pointing at the original). Additive.
    /// </summary>
    internal partial class CustodyAdjustmentDisputes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisputeNote",
                schema: "finance",
                table: "CustodyAdjustment",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DisputedAt",
                schema: "finance",
                table: "CustodyAdjustment",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HolderUserId",
                schema: "finance",
                table: "CustodyAdjustment",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionNote",
                schema: "finance",
                table: "CustodyAdjustment",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAt",
                schema: "finance",
                table: "CustodyAdjustment",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResolvedBy",
                schema: "finance",
                table: "CustodyAdjustment",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReversesAdjustmentId",
                schema: "finance",
                table: "CustodyAdjustment",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "finance",
                table: "CustodyAdjustment",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<decimal>(
                name: "Shortfall",
                schema: "finance",
                table: "CustodyAdjustment",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "finance",
                table: "CustodyAdjustment",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyAdjustment_Status",
                schema: "finance",
                table: "CustodyAdjustment",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_CustodyAdjustment_Reverses",
                schema: "finance",
                table: "CustodyAdjustment",
                column: "ReversesAdjustmentId",
                unique: true,
                filter: "[ReversesAdjustmentId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustodyAdjustment_Dispute",
                schema: "finance",
                table: "CustodyAdjustment",
                sql: "([Status] = 'Posted' AND [DisputedAt] IS NULL) OR ([Status] <> 'Posted' AND [DisputedAt] IS NOT NULL AND [DisputeNote] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustodyAdjustment_Resolved",
                schema: "finance",
                table: "CustodyAdjustment",
                sql: "([Status] IN ('Posted', 'Disputed') AND [ResolvedAt] IS NULL AND [ResolvedBy] IS NULL) OR ([Status] IN ('Upheld', 'Reversed') AND [ResolvedAt] IS NOT NULL AND [ResolvedBy] IS NOT NULL AND [ResolutionNote] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustodyAdjustment_Shortfall",
                schema: "finance",
                table: "CustodyAdjustment",
                sql: "[Shortfall] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustodyAdjustment_Status",
                schema: "finance",
                table: "CustodyAdjustment",
                sql: "[Status] IN ('Posted', 'Disputed', 'Upheld', 'Reversed')");

            migrationBuilder.AddForeignKey(
                name: "FK_CustodyAdjustment_CustodyAdjustment_ReversesAdjustmentId",
                schema: "finance",
                table: "CustodyAdjustment",
                column: "ReversesAdjustmentId",
                principalSchema: "finance",
                principalTable: "CustodyAdjustment",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustodyAdjustment_CustodyAdjustment_ReversesAdjustmentId",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropIndex(
                name: "IX_CustodyAdjustment_Status",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropIndex(
                name: "UX_CustodyAdjustment_Reverses",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CustodyAdjustment_Dispute",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CustodyAdjustment_Resolved",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CustodyAdjustment_Shortfall",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CustodyAdjustment_Status",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropColumn(
                name: "DisputeNote",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropColumn(
                name: "DisputedAt",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropColumn(
                name: "HolderUserId",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropColumn(
                name: "ResolutionNote",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropColumn(
                name: "ResolvedBy",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropColumn(
                name: "ReversesAdjustmentId",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropColumn(
                name: "Shortfall",
                schema: "finance",
                table: "CustodyAdjustment");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "finance",
                table: "CustodyAdjustment");
        }
    }
}
