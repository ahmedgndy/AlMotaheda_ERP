using System.Globalization;
using System.Security.Claims;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Features.Ledger;
using AlMotaheda.Modules.Finance.Requests;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Reports.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Integration;

/// <summary>
/// The Finance reports, computed from the ledger and the payment requests with the same queries as the ledger and
/// request screens (<see cref="ListLedgerEntries.Query"/>, <see cref="PaymentRequestViews.Query"/>), so a report's
/// total is the ledger's total.
/// </summary>
internal sealed class FinanceReports(FinanceDbContext db, IVisibleProjects projects, IEnumerable<IPaymentSource> sources) : IReportSource
{
    public const string CashMovement = "FIN-CASH";
    public const string Ledger = "FIN-LEDGER";
    public const string Requests = "FIN-REQUESTS";
    public const string Reconciliation = "FIN-RECON";

    private static readonly ReportPermission[] FinanceMoney = [new("finance", ReportActions.SeeMoney)];

    private static readonly ReportFilter From = new("from", ReportFilterTypes.Date, true, "من تاريخ", "From");
    private static readonly ReportFilter To = new("to", ReportFilterTypes.Date, true, "إلى تاريخ", "To");
    private static readonly ReportFilter Project = new("projectId", ReportFilterTypes.Id, false, "المشروع", "Project");

    public IReadOnlyList<ReportDefinition> Reports { get; } =
    [
        new(CashMovement, "حركة النقدية (يومي / أسبوعي / شهري)", "Cash movement (daily / weekly / monthly)", ReportCategories.Finance, FinanceMoney,
            [From, To, new("period", ReportFilterTypes.Option, false, "الفترة", "Period", ["Day", "Week", "Month"]), Project],
            [
                new("period", "الفترة", "Period", ReportColumnTypes.Text),
                new("periodStart", "من", "From", ReportColumnTypes.Date),
                new("periodEnd", "إلى", "To", ReportColumnTypes.Date),
                new("moneyIn", "المقبوضات", "Money in", ReportColumnTypes.Money, Total: true),
                new("moneyOut", "المدفوعات", "Money out", ReportColumnTypes.Money, Total: true),
                new("internalOut", "عهد مسلّمة (تحويل داخلي)", "Custody advances (internal)", ReportColumnTypes.Money, Total: true),
                new("internalIn", "عهد مرتجعة (تحويل داخلي)", "Custody returns (internal)", ReportColumnTypes.Money, Total: true),
                new("paidFromCustody", "مدفوع من العهد", "Paid from custodies", ReportColumnTypes.Money, Total: true),
                new("net", "صافي حركة الخزينة", "Treasury net", ReportColumnTypes.Money, Total: true),
                new("entries", "عدد الحركات", "Entries", ReportColumnTypes.Integer, Total: true),
            ]),
        new(Ledger, "دفتر الحركات", "Transaction ledger", ReportCategories.Finance, FinanceMoney,
            [From, To, Project, new("account", ReportFilterTypes.Option, false, "الحساب", "Account", Enum.GetNames<LedgerAccount>()),
             new("counterpartyType", ReportFilterTypes.Option, false, "نوع المستفيد", "Beneficiary type", PayeeTypes.All)],
            [
                new("entryDate", "التاريخ", "Date", ReportColumnTypes.Date),
                new("entryId", "رقم الحركة", "Entry", ReportColumnTypes.Integer),
                new("account", "الحساب", "Account", ReportColumnTypes.Text),
                new("purpose", "الغرض", "Purpose", ReportColumnTypes.Text),
                new("kind", "النوع", "Kind", ReportColumnTypes.Text),
                new("counterpartyType", "نوع المستفيد", "Beneficiary type", ReportColumnTypes.Text),
                new("counterparty", "المستفيد", "Beneficiary", ReportColumnTypes.Text),
                new("projectId", "المشروع", "Project", ReportColumnTypes.Integer),
                new("request", "طلب الصرف / التحويل", "Payment request / transfer", ReportColumnTypes.Text),
                new("reversal", "عكس قيد", "Reversal", ReportColumnTypes.Text),
                new("amount", "المبلغ", "Amount", ReportColumnTypes.Money, Total: true),
            ]),
        new(Requests, "طلبات الصرف حسب الحالة", "Payment requests by status", ReportCategories.Finance, [new("payments", ReportActions.SeeMoney)],
            [new("from", ReportFilterTypes.Date, false, "من تاريخ", "From"), new("to", ReportFilterTypes.Date, false, "إلى تاريخ", "To"),
             new("status", ReportFilterTypes.Option, false, "الحالة", "Status", Enum.GetNames<PaymentRequestStatus>()), Project,
             new("payeeType", ReportFilterTypes.Option, false, "نوع المستفيد", "Beneficiary type", PayeeTypes.All)],
            [
                new("number", "رقم الطلب", "Request", ReportColumnTypes.Text),
                new("requestedDate", "تاريخ الطلب", "Requested", ReportColumnTypes.Date),
                new("status", "الحالة", "Status", ReportColumnTypes.Text),
                new("payeeType", "نوع المستفيد", "Beneficiary type", ReportColumnTypes.Text),
                new("payee", "المستفيد", "Beneficiary", ReportColumnTypes.Text),
                new("purpose", "الغرض", "Purpose", ReportColumnTypes.Text),
                new("method", "طريقة الدفع", "Method", ReportColumnTypes.Text),
                new("projectId", "المشروع", "Project", ReportColumnTypes.Integer),
                new("paidOn", "تاريخ الدفع", "Paid on", ReportColumnTypes.Date),
                new("amount", "المبلغ", "Amount", ReportColumnTypes.Money, Total: true),
            ]),
        new(Reconciliation, "المطابقة: طلبات الصرف مقابل الدفتر", "Reconciliation: payment requests against the ledger", ReportCategories.Finance, FinanceMoney,
            [From, To],
            [
                new("number", "رقم الطلب", "Request", ReportColumnTypes.Text),
                new("status", "الحالة", "Status", ReportColumnTypes.Text),
                new("paidOn", "تاريخ الدفع", "Paid on", ReportColumnTypes.Date),
                new("requestAmount", "مبلغ الطلب", "Request amount", ReportColumnTypes.Money, Total: true),
                new("expected", "المتوقع في الدفتر", "Expected in ledger", ReportColumnTypes.Money, Total: true),
                new("ledger", "في الدفتر", "In ledger", ReportColumnTypes.Money, Total: true),
                new("difference", "الفرق", "Difference", ReportColumnTypes.Money, Total: true),
                new("result", "النتيجة", "Result", ReportColumnTypes.Text),
            ]),
    ];

    public async Task<ReportData?> RunAsync(string code, ReportQuery query, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        var visible = await projects.IdsAsync(caller, cancellationToken);
        if (query.Id("projectId") is { } projectId && visible is not null && !visible.Contains(projectId))
        {
            return null;
        }

        return code switch
        {
            CashMovement => await CashAsync(query, visible, cancellationToken),
            Ledger => await LedgerAsync(query, visible, cancellationToken),
            Requests => await RequestsAsync(query, visible, cancellationToken),
            Reconciliation => await ReconcileAsync(query, visible, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a Finance report."),
        };
    }

    private async Task<ReportData> CashAsync(ReportQuery query, IReadOnlySet<int>? visible, CancellationToken cancellationToken)
    {
        var entries = await ListLedgerEntries.Query(db, new ListLedgerEntries.Filter(query.Date("from"), query.Date("to"), query.Id("projectId"), null, null, null, visible))
            .Select(entry => new { entry.EntryDate, entry.Amount, entry.Purpose, entry.Account })
            .ToListAsync(cancellationToken);
        var period = query.Text("period") ?? "Day";

        var rows = entries
            .GroupBy(entry => PeriodStart(entry.EntryDate, period))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                // Payments are the money out of the treasury less their reversals; custody advances and returns are
                // internal transfers, shown apart and never counted as expense; money in is what Executives deposited
                // (owner capital, loans, client payments), never income.
                var moneyIn = group.Where(entry => entry.Purpose == PaymentPurposes.TreasuryDeposit).Sum(entry => entry.Amount);
                var payments = group.Where(entry => !PaymentPurposes.IsInternalTransfer(entry.Purpose) && entry.Purpose != PaymentPurposes.TreasuryDeposit && entry.Account != LedgerAccount.Custody);
                var moneyOut = -payments.Sum(entry => entry.Amount);
                var internalOut = -group.Where(entry => PaymentPurposes.IsInternalTransfer(entry.Purpose) && entry.Amount < 0).Sum(entry => entry.Amount);
                var internalIn = group.Where(entry => PaymentPurposes.IsInternalTransfer(entry.Purpose) && entry.Amount > 0).Sum(entry => entry.Amount);
                var fromCustody = -group.Where(entry => entry.Account == LedgerAccount.Custody).Sum(entry => entry.Amount);
                return new ReportRow(new Dictionary<string, object?>
                {
                    ["period"] = PeriodLabel(group.Key, period),
                    ["periodStart"] = group.Key,
                    ["periodEnd"] = PeriodEnd(group.Key, period),
                    ["moneyIn"] = moneyIn,
                    ["moneyOut"] = moneyOut,
                    ["internalOut"] = internalOut,
                    ["internalIn"] = internalIn,
                    ["paidFromCustody"] = fromCustody,
                    ["net"] = moneyIn - moneyOut - internalOut + internalIn,
                    ["entries"] = group.Count(),
                }, new ReportLink("report", Report: Ledger, Filters: new Dictionary<string, string>
                {
                    ["from"] = Iso(group.Key),
                    ["to"] = Iso(PeriodEnd(group.Key, period)),
                }));
            })
            .ToList();

        return new ReportData(rows, "Money in is what was deposited into the treasury (owner capital, loans, client payments): money in, never income; client income itself is not recorded in the system yet. Internal transfers (custody advances and returns) move cash inside the company and are never an expense; payments out of a custody were already drawn from the treasury. Weeks start on Saturday.");
    }

    private async Task<ReportData> LedgerAsync(ReportQuery query, IReadOnlySet<int>? visible, CancellationToken cancellationToken)
    {
        LedgerAccount? account = query.Text("account") is { } text ? Enum.Parse<LedgerAccount>(text) : null;
        var entries = await ListLedgerEntries.Query(db, new ListLedgerEntries.Filter(
                query.Date("from"), query.Date("to"), query.Id("projectId"), account, query.Text("counterpartyType"), null, visible))
            .GroupJoin(db.PaymentRequests, entry => entry.PaymentRequestId, request => request.Id, (entry, requests) => new { entry, requests })
            .SelectMany(row => row.requests.DefaultIfEmpty(), (row, request) => new { row.entry, Number = request != null ? request.Number : null, SourceType = request != null ? request.SourceType : row.entry.SourceType })
            .GroupJoin(db.CustodyTransfers, row => row.entry.CustodyTransferId, transfer => transfer.Id, (row, transfers) => new { row.entry, row.Number, row.SourceType, transfers })
            .SelectMany(row => row.transfers.DefaultIfEmpty(), (row, transfer) => new { row.entry, row.Number, row.SourceType, TransferNumber = transfer != null ? transfer.Number : null })
            .OrderBy(row => row.entry.EntryDate).ThenBy(row => row.entry.Id)
            .ToListAsync(cancellationToken);
        var names = await PayeeNamesAsync(entries.Where(row => row.entry.CounterpartyType != PayeeTypes.CustodyAccount).Select(row => (row.SourceType, row.entry.CounterpartyId)), cancellationToken);
        var depositIds = entries.Where(row => row.entry.TreasuryDepositId != null).Select(row => row.entry.TreasuryDepositId!.Value).Distinct().ToList();
        var deposits = depositIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.TreasuryDeposits.AsNoTracking().Where(deposit => depositIds.Contains(deposit.Id)).ToDictionaryAsync(deposit => deposit.Id, deposit => $"{deposit.Number} {deposit.Source}", cancellationToken);
        var custodyIds = entries.Where(row => row.entry.CounterpartyType == PayeeTypes.CustodyAccount).Select(row => row.entry.CounterpartyId).Distinct().ToList();
        var custodies = custodyIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.CustodyAccounts.AsNoTracking().Where(account => custodyIds.Contains(account.Id)).ToDictionaryAsync(account => account.Id, account => $"{account.Number} {account.NameEn}", cancellationToken);

        return new ReportData(entries.Select(row => new ReportRow(new Dictionary<string, object?>
        {
            ["entryDate"] = row.entry.EntryDate,
            ["entryId"] = (int)row.entry.Id,
            ["account"] = row.entry.Account.ToString(),
            ["purpose"] = row.entry.Purpose,
            ["kind"] = row.entry.Purpose == PaymentPurposes.TreasuryDeposit ? "Deposit" : PaymentPurposes.IsInternalTransfer(row.entry.Purpose) ? "Internal transfer" : "Payment",
            ["counterpartyType"] = row.entry.CounterpartyType,
            ["counterparty"] = row.entry.TreasuryDepositId is { } depositId
                ? deposits.GetValueOrDefault(depositId) ?? $"#{depositId}"
                : row.entry.CounterpartyType == PayeeTypes.CustodyAccount
                ? custodies.GetValueOrDefault(row.entry.CounterpartyId) ?? $"#{row.entry.CounterpartyId}"
                : names.GetValueOrDefault((row.SourceType, row.entry.CounterpartyId))?.NameEn ?? $"#{row.entry.CounterpartyId}",
            ["projectId"] = row.entry.ProjectId,
            ["request"] = row.Number ?? row.TransferNumber,
            ["reversal"] = row.entry.ReversesEntryId is { } reversed ? $"#{reversed}" : null,
            ["amount"] = row.entry.Amount,
        }, row.entry.PaymentRequestId is { } requestId
            ? new ReportLink("paymentRequest", requestId, row.entry.ProjectId)
            : new ReportLink("custodyTransfer", row.entry.CustodyTransferId, row.entry.ProjectId))).ToList());
    }

    private async Task<ReportData> RequestsAsync(ReportQuery query, IReadOnlySet<int>? visible, CancellationToken cancellationToken)
    {
        PaymentRequestStatus? status = query.Text("status") is { } text ? Enum.Parse<PaymentRequestStatus>(text) : null;
        var requests = PaymentRequestViews.Query(db, new RequestFilter(status, query.Text("payeeType"), null, query.Id("projectId"), null, visible));
        if (query.Date("from") is { } from)
        {
            requests = requests.Where(request => request.RequestedDate >= from);
        }

        if (query.Date("to") is { } to)
        {
            requests = requests.Where(request => request.RequestedDate <= to);
        }

        var list = await requests.ToListAsync(cancellationToken);
        var names = await PayeeNamesAsync(list.Select(request => (request.SourceType, request.PayeeId)), cancellationToken);

        return new ReportData(list.OrderBy(request => request.Status).ThenBy(request => request.Id).Select(request => new ReportRow(new Dictionary<string, object?>
        {
            ["number"] = request.Number,
            ["requestedDate"] = request.RequestedDate,
            ["status"] = request.Status.ToString(),
            ["payeeType"] = request.PayeeType,
            ["payee"] = names.GetValueOrDefault((request.SourceType, request.PayeeId))?.NameEn ?? $"#{request.PayeeId}",
            ["purpose"] = request.Purpose,
            ["method"] = request.Method,
            ["projectId"] = request.ProjectId,
            ["paidOn"] = request.PaidOn,
            ["amount"] = request.Amount,
        }, new ReportLink("paymentRequest", request.Id, request.ProjectId))).ToList());
    }

    /// <summary>
    /// A Paid request must have ledger entries adding up to −amount; a Reversed one to 0; no other request may have
    /// entries. Any other figure is a mismatch to look into.
    /// </summary>
    private async Task<ReportData> ReconcileAsync(ReportQuery query, IReadOnlySet<int>? visible, CancellationToken cancellationToken)
    {
        var from = query.Date("from")!.Value;
        var to = query.Date("to")!.Value;
        var requests = db.PaymentRequests.AsNoTracking()
            .Where(request => (request.PaidOn >= from && request.PaidOn <= to)
                              || db.LedgerEntries.Any(entry => entry.PaymentRequestId == request.Id && entry.EntryDate >= from && entry.EntryDate <= to));
        if (visible is not null)
        {
            requests = requests.Where(request => request.ProjectId != null && visible.Contains(request.ProjectId.Value));
        }

        var rows = await requests
            .Select(request => new
            {
                request.Id,
                request.Number,
                request.Status,
                request.PaidOn,
                request.Amount,
                request.ProjectId,
                Ledger = db.LedgerEntries.Where(entry => entry.PaymentRequestId == request.Id).Sum(entry => (decimal?)entry.Amount) ?? 0,
            })
            .OrderBy(request => request.Id)
            .ToListAsync(cancellationToken);

        return new ReportData(rows.Select(row =>
        {
            var expected = row.Status switch
            {
                PaymentRequestStatus.Paid => -row.Amount,
                _ => 0m,
            };
            var difference = row.Ledger - expected;
            return new ReportRow(new Dictionary<string, object?>
            {
                ["number"] = row.Number,
                ["status"] = row.Status.ToString(),
                ["paidOn"] = row.PaidOn,
                ["requestAmount"] = row.Amount,
                ["expected"] = expected,
                ["ledger"] = row.Ledger,
                ["difference"] = difference,
                ["result"] = difference == 0 ? "Matched" : "Mismatch",
            }, new ReportLink("paymentRequest", row.Id, row.ProjectId));
        }).ToList());
    }

    private async Task<Dictionary<(string SourceType, int PayeeId), PayeeName>> PayeeNamesAsync(IEnumerable<(string SourceType, int PayeeId)> payees, CancellationToken cancellationToken)
    {
        var names = new Dictionary<(string, int), PayeeName>();
        foreach (var group in payees.Distinct().GroupBy(payee => payee.SourceType))
        {
            if (sources.FirstOrDefault(candidate => candidate.SourceType == group.Key) is not { } source)
            {
                continue;
            }

            foreach (var (id, name) in await source.PayeeNamesAsync(group.Select(payee => payee.PayeeId).ToList(), cancellationToken))
            {
                names[(group.Key, id)] = name;
            }
        }

        return names;
    }

    /// <summary>Weeks start on Saturday, the first working day in Egypt (Needs Confirmation).</summary>
    internal static DateOnly PeriodStart(DateOnly date, string period) => period switch
    {
        "Week" => date.AddDays(-(((int)date.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7)),
        "Month" => new DateOnly(date.Year, date.Month, 1),
        _ => date,
    };

    internal static DateOnly PeriodEnd(DateOnly start, string period) => period switch
    {
        "Week" => start.AddDays(6),
        "Month" => start.AddMonths(1).AddDays(-1),
        _ => start,
    };

    private static string PeriodLabel(DateOnly start, string period) => period switch
    {
        "Week" => $"{Iso(start)} → {Iso(PeriodEnd(start, period))}",
        "Month" => start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        _ => Iso(start),
    };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
