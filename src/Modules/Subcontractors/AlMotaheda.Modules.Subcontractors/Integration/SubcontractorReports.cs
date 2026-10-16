using System.Security.Claims;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Reports.Contracts;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features.Accounts;
using AlMotaheda.Modules.Subcontractors.Features.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Integration;

/// <summary>
/// The subcontractor account: per contract, its value, executed work, payments, retention and what is due now,
/// computed by <see cref="ContractAccount"/> exactly as the contracts screen does.
/// </summary>
internal sealed class SubcontractorReports(SubcontractorsDbContext db, IVisibleProjects projects, IProjectAccess access) : IReportSource
{
    public const string Account = "SUB-ACCOUNT";

    public IReadOnlyList<ReportDefinition> Reports { get; } =
    [
        new(Account, "حساب مقاولي الباطن ومدفوعاتهم", "Subcontractor account and payments", ReportCategories.Parties,
            [new("subcontractors", ReportActions.SeeMoney)],
            [
                new("projectId", ReportFilterTypes.Id, false, "المشروع", "Project"),
                new("subcontractorId", ReportFilterTypes.Id, false, "مقاول الباطن", "Subcontractor"),
            ],
            [
                new("projectId", "المشروع", "Project", ReportColumnTypes.Integer),
                new("subcontractor", "مقاول الباطن", "Subcontractor", ReportColumnTypes.Text),
                new("contractNo", "رقم العقد", "Contract", ReportColumnTypes.Text),
                new("status", "الحالة", "Status", ReportColumnTypes.Text),
                new("value", "قيمة العقد", "Contract value", ReportColumnTypes.Money, Total: true),
                new("executed", "المنفذ المعتمد", "Executed", ReportColumnTypes.Money, Total: true),
                new("paid", "المدفوع", "Paid", ReportColumnTypes.Money, Total: true),
                new("retention", "المحتجز", "Retention held", ReportColumnTypes.Money, Total: true),
                new("advance", "دفعة مقدمة غير مستخدمة", "Unapplied advance", ReportColumnTypes.Money, Total: true),
                new("dueNow", "المستحق الآن", "Due now", ReportColumnTypes.Money, Total: true),
            ]),
    ];

    public async Task<ReportData?> RunAsync(string code, ReportQuery query, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        if (code != Account)
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Not a Subcontractors report.");
        }

        IReadOnlyCollection<int> projectIds;
        if (query.Id("projectId") is { } projectId)
        {
            if (await access.FindVisibleAsync(caller, projectId, cancellationToken) is null)
            {
                return null;
            }

            projectIds = [projectId];
        }
        else
        {
            var visible = await projects.IdsAsync(caller, cancellationToken);
            projectIds = visible is not null
                ? visible.ToList()
                : await db.SubContracts.AsNoTracking().Select(contract => contract.ProjectId).Distinct().ToListAsync(cancellationToken);
        }

        var wanted = query.Id("subcontractorId");
        var rows = new List<ReportRow>();
        foreach (var id in projectIds.Order())
        {
            var contracts = await ContractQueries.Rows(db, id).ToListAsync(cancellationToken);
            if (wanted is not null)
            {
                contracts = contracts.Where(contract => contract.SubcontractorId == wanted).ToList();
            }

            if (contracts.Count == 0)
            {
                continue;
            }

            var work = (await WorkQueries.LoadWorkAsync(db, id, contractId: null, cancellationToken)).ToLookup(row => row.ContractId);
            var payments = (await WorkQueries.Payments(db, id).ToListAsync(cancellationToken)).ToLookup(row => row.ContractId);
            rows.AddRange(contracts.OrderBy(contract => contract.SubcontractorNameEn).Select(contract =>
            {
                var account = ContractAccount.Compute(contract.Terms, work[contract.Id], payments[contract.Id]);
                return new ReportRow(new Dictionary<string, object?>
                {
                    ["projectId"] = contract.ProjectId,
                    ["subcontractor"] = contract.SubcontractorNameEn,
                    ["contractNo"] = contract.ContractNo ?? $"#{contract.Id}",
                    ["status"] = contract.Status.ToString(),
                    ["value"] = account.Value,
                    ["executed"] = account.Executed,
                    ["paid"] = account.PaidTotal,
                    ["retention"] = account.RetentionHeld,
                    ["advance"] = account.UnappliedAdvance,
                    ["dueNow"] = account.DueNow,
                }, new ReportLink("subcontract", contract.Id, contract.ProjectId));
            }));
        }

        return new ReportData(rows);
    }
}
