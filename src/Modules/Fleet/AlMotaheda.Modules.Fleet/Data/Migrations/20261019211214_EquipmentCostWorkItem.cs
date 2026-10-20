using AlMotaheda.Modules.Fleet.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Fleet.Data.Migrations
{
    /// <summary>
    /// An equipment cost can name the work item the machine worked for (a main item or a sub-item of the same
    /// project), so machinery reaches the work-item cost breakdown. Only adds: no row is changed. Existing costs keep
    /// no work item (NULL): they stay costs of the whole site, exactly as before.
    /// </summary>
    /// <remarks>Down drops the links: the costs stay, on no work item.</remarks>
    internal partial class EquipmentCostWorkItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProjectItemId",
                schema: "fleet",
                table: "EquipmentCost",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentCost_Project_Item",
                schema: "fleet",
                table: "EquipmentCost",
                columns: new[] { "ProjectId", "ProjectItemId" });

            // The work item is a Projects record: this key is added by hand (Projects owns the table). It is composite,
            // on the item's (ProjectId, Id) key, so the database refuses a work item of another project. A row with no
            // work item (NULL) is not checked.
            migrationBuilder.AddForeignKey(
                name: EquipmentCostConfiguration.ProjectItemForeignKey,
                schema: "fleet",
                table: "EquipmentCost",
                columns: new[] { "ProjectId", "ProjectItemId" },
                principalSchema: "projects",
                principalTable: "ProjectItem",
                principalColumns: new[] { "ProjectId", "Id" },
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: EquipmentCostConfiguration.ProjectItemForeignKey,
                schema: "fleet",
                table: "EquipmentCost");

            migrationBuilder.DropIndex(
                name: "IX_EquipmentCost_Project_Item",
                schema: "fleet",
                table: "EquipmentCost");

            migrationBuilder.DropColumn(
                name: "ProjectItemId",
                schema: "fleet",
                table: "EquipmentCost");
        }
    }
}
