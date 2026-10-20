using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Integration;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Tests.Unit;

/// <summary>
/// "Is this contract or payment on this project?" translates to SQL and filters by the project; a payment through the
/// contract it was made under. No database needed.
/// </summary>
public sealed class RecordOwnershipQueryTests
{
    private static SubcontractorsDbContext CreateContext() => new SubcontractorsDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void A_contract_is_on_its_own_project_only()
    {
        using var db = CreateContext();

        var sql = SubcontractRecordOwnership.Query(db, 3, ProjectRecordKinds.SubContract, [5])!.ToQueryString();

        Assert.Contains("[sub].[SubContract]", sql);
        Assert.Contains("[ProjectId] = @", sql);
    }

    [Fact]
    public void A_payment_is_on_the_project_of_its_contract()
    {
        using var db = CreateContext();

        var sql = SubcontractRecordOwnership.Query(db, 3, ProjectRecordKinds.SubPayment, [5])!.ToQueryString();

        Assert.Contains("[sub].[SubPayment]", sql);
        Assert.Contains("[sub].[SubContract]", sql);
        Assert.Contains("[ProjectId] = @", sql);
        Assert.Null(SubcontractRecordOwnership.Query(db, 3, ProjectRecordKinds.Equipment, [5]));
    }

    [Fact]
    public void A_contract_variation_is_on_its_own_project_only()
    {
        using var db = CreateContext();

        var sql = SubcontractRecordOwnership.Query(db, 3, ProjectRecordKinds.ContractVariation, [5])!.ToQueryString();

        Assert.Contains("[sub].[ContractVariation]", sql);
        Assert.Contains("[ProjectId] = @", sql);
    }
}
