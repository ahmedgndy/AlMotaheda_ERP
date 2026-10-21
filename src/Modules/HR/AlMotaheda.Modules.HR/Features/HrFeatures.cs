using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Features.Assignments;
using AlMotaheda.Modules.HR.Features.Employees;
using AlMotaheda.Modules.HR.Features.Payroll;
using AlMotaheda.Modules.HR.Features.Referrals;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.HR.Features;

/// <summary>Wires the HR module's features. See docs/modernization/10-hr-design.md.</summary>
internal static class HrFeatures
{
    public static void AddServices(IServiceCollection services) => services.AddSingleton<HrLog>();

    /// <param name="api">The shared <c>/api</c> group. Every route starts with <c>/hr</c>.</param>
    public static void Map(IEndpointRouteBuilder api)
    {
        ListDepartments.Map(api);
        SaveDepartment.Map(api);

        ListEmployees.Map(api);
        GetEmployee.Map(api);
        SaveEmployee.Map(api);
        EndEmployee.Map(api);
        EmployeeDocuments.Map(api);

        SalaryStructures.Map(api);
        PayrollInputs.Map(api);

        ReadSalarySheets.Map(api);
        PrepareSalarySheet.Map(api);
        ReviewSalarySheet.Map(api);
        RequestSalaryPayment.Map(api);

        EmployeeAssignments.Map(api);
        HrReferralEndpoints.Map(api);
    }
}
