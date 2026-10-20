using System;
using AlMotaheda.Modules.Subcontractors.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Subcontractors.Data.Migrations
{
    /// <summary>
    /// A subcontract can name the main work item it is for, so one company can hold one engagement per main work
    /// item of a project; and a contract's value can change through approved variations. Only adds: no row is
    /// changed or removed. The old "one contract per company and project" index becomes "one per company, project
    /// and work item"; existing contracts have no work item (NULL, which the unique index treats as one value), so
    /// they stay unique per company and project exactly as before.
    /// </summary>
    /// <remarks>Down fails if a company was given two engagements on one project since; end or merge them first.</remarks>
    internal partial class EngagementsAndVariations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_SubContract_Project_Subcontractor",
                schema: "sub",
                table: "SubContract");

            migrationBuilder.AddColumn<int>(
                name: "ProjectItemId",
                schema: "sub",
                table: "SubContract",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ContractVariation",
                schema: "sub",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContractId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RequestedBy = table.Column<int>(type: "int", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecidedBy = table.Column<int>(type: "int", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractVariation", x => x.Id);
                    table.CheckConstraint("CK_ContractVariation_Amount", "[Amount] <> 0");
                    table.CheckConstraint("CK_ContractVariation_Decision", "([Status] = 'Pending' AND [DecidedAt] IS NULL AND [DecidedBy] IS NULL) OR ([Status] <> 'Pending' AND [DecidedAt] IS NOT NULL AND [DecidedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_ContractVariation_Status", "[Status] IN ('Pending', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_ContractVariation_SubContract_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "sub",
                        principalTable: "SubContract",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubContract_ProjectItemId",
                schema: "sub",
                table: "SubContract",
                column: "ProjectItemId");

            migrationBuilder.CreateIndex(
                name: "UX_SubContract_Project_Subcontractor_Item",
                schema: "sub",
                table: "SubContract",
                columns: new[] { "ProjectId", "SubcontractorId", "ProjectItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractVariation_Contract_Status",
                schema: "sub",
                table: "ContractVariation",
                columns: new[] { "ContractId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_ContractVariation_Pending",
                schema: "sub",
                table: "ContractVariation",
                column: "ContractId",
                unique: true,
                filter: "[Status] = 'Pending'");

            // The work item and the project are Projects records: these keys are added by hand (Projects owns the tables).
            migrationBuilder.AddForeignKey(
                name: SubContractConfiguration.ProjectItemForeignKey,
                schema: "sub",
                table: "SubContract",
                column: "ProjectItemId",
                principalSchema: "projects",
                principalTable: "ProjectItem",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: ContractVariationConfiguration.ProjectForeignKey,
                schema: "sub",
                table: "ContractVariation",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: SubContractConfiguration.ProjectItemForeignKey,
                schema: "sub",
                table: "SubContract");

            migrationBuilder.DropTable(
                name: "ContractVariation",
                schema: "sub");

            migrationBuilder.DropIndex(
                name: "IX_SubContract_ProjectItemId",
                schema: "sub",
                table: "SubContract");

            migrationBuilder.DropIndex(
                name: "UX_SubContract_Project_Subcontractor_Item",
                schema: "sub",
                table: "SubContract");

            migrationBuilder.DropColumn(
                name: "ProjectItemId",
                schema: "sub",
                table: "SubContract");

            migrationBuilder.CreateIndex(
                name: "UX_SubContract_Project_Subcontractor",
                schema: "sub",
                table: "SubContract",
                columns: new[] { "ProjectId", "SubcontractorId" },
                unique: true);
        }
    }
}
