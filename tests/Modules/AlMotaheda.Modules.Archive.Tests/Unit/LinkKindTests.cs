using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Archive.Tests.Unit;

/// <summary>
/// A link type is checked by asking the module that owns the record kind of the same name, so every link type must
/// be a known kind (otherwise every link of that type would be refused).
/// </summary>
public sealed class LinkKindTests
{
    [Fact]
    public void Every_link_type_is_a_project_record_kind()
    {
        string[] kinds =
        [
            ProjectRecordKinds.Expense, ProjectRecordKinds.SupplierPayment, ProjectRecordKinds.DailyReport, ProjectRecordKinds.ReportRequest,
            ProjectRecordKinds.Budget, ProjectRecordKinds.LaborSheet, ProjectRecordKinds.SubContract, ProjectRecordKinds.SubPayment,
            ProjectRecordKinds.Equipment, ProjectRecordKinds.EquipmentCost,
        ];

        Assert.Equal(kinds.Order(StringComparer.Ordinal), Enum.GetNames<LinkedEntityType>().Order(StringComparer.Ordinal));
    }
}
