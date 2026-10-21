using System.Security.Claims;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Features.Custody;
using AlMotaheda.Modules.Finance.Features.Payroll;
using AlMotaheda.Modules.Reports.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Integration;

/// <summary>
/// The treasury and custody reports, from the same rows and arithmetic as the screens (<see cref="TreasuryQueries"/>,
/// <see cref="CustodyLedgers"/>, <see cref="PayrollBatchViews"/>), so a report's figure is the screen's figure and
/// every balance reconciles to the ledger. A custody transfer never appears as an expense.
/// </summary>
internal sealed class CustodyReports(
    FinanceDbContext db,
    CustodyLedgers ledgers,
    PayrollBatchViews batches,
    IUserDirectory users,
    IEnumerable<IPaymentSource> sources) : IReportSource
{
    public const string Treasury = "FIN-TREASURY";
    public const string Custodians = "FIN-CUSTODY";
    public const string Payroll = "FIN-PAYROLL-BATCH";
    public const string PartyPayments = "FIN-PARTY-PAYMENTS";
    public const string Reconciliation = "FIN-CUSTODY-RECON";

    /// <summary>Every money step anyone initiated in a period, for the Executive's supervision (section 13.A).</summary>
    public const string Activity = "FIN-ACTIVITY";

    private static readonly string[] ActivityKinds = ["PaymentRequest", "CustodyTransfer", "CustodyAdjustment", "CustodyCashCount"];

    private static readonly ReportPermission[] FinanceMoney = [new("finance", ReportActions.SeeMoney)];
    private static readonly ReportPermission[] CustodyMoney = [new("custody", ReportActions.SeeMoney), new("finance", ReportActions.SeeMoney)];

    public IReadOnlyList<ReportDefinition> Reports { get; } =
    [
        new(Treasury, "أرصدة الخزينة", "Treasury balances", ReportCategories.Finance, FinanceMoney, [],
            [
                new("account", "الحساب", "Account", ReportColumnTypes.Text),
                new("openingDate", "تاريخ الافتتاح", "Opening date", ReportColumnTypes.Date),
                new("openingBalance", "رصيد الافتتاح", "Opening balance", ReportColumnTypes.Money, Total: true),
                new("moneyOut", "المدفوعات", "Payments out", ReportColumnTypes.Money, Total: true),
                new("internalOut", "عهد مسلّمة", "Custody advances", ReportColumnTypes.Money, Total: true),
                new("internalIn", "عهد مرتجعة", "Custody returns", ReportColumnTypes.Money, Total: true),
                new("balance", "الرصيد", "Balance", ReportColumnTypes.Money, Total: true),
                new("reserved", "محجوز", "Reserved", ReportColumnTypes.Money, Total: true),
                new("available", "المتاح", "Available", ReportColumnTypes.Money, Total: true),
            ]),
        new(Custodians, "الأموال حسب أمين العهدة", "Funds by custodian", ReportCategories.Finance, CustodyMoney,
            [new("kind", ReportFilterTypes.Option, false, "النوع", "Kind", CustodyAccountKinds.All), new("status", ReportFilterTypes.Option, false, "الحالة", "Status", Enum.GetNames<CustodyAccountStatus>())],
            [
                new("number", "رقم الحساب", "Account", ReportColumnTypes.Text),
                new("kind", "النوع", "Kind", ReportColumnTypes.Text),
                new("holder", "أمين العهدة", "Custodian", ReportColumnTypes.Text),
                new("name", "الاسم", "Name", ReportColumnTypes.Text),
                new("referenceId", "المرجع", "Reference", ReportColumnTypes.Integer),
                new("status", "الحالة", "Status", ReportColumnTypes.Text),
                new("posted", "الرصيد المرحّل", "Posted balance", ReportColumnTypes.Money, Total: true),
                new("pending", "التزامات معلّقة", "Pending exposure", ReportColumnTypes.Money, Total: true),
                new("reserved", "محجوز", "Reserved", ReportColumnTypes.Money, Total: true),
                new("inTransit", "في الطريق", "In transit", ReportColumnTypes.Money, Total: true),
                new("available", "المتاح", "Available", ReportColumnTypes.Money, Total: true),
            ]),
        new(Payroll, "تمويل الأجور: ممّول / موزّع / مدفوع / متبقٍ", "Payroll funding: funded / distributed / paid / unpaid", ReportCategories.Finance, CustodyMoney,
            [new("status", ReportFilterTypes.Option, false, "الحالة", "Status", Enum.GetNames<PayrollBatchStatus>())],
            [
                new("number", "الدفعة", "Batch", ReportColumnTypes.Text),
                new("kind", "النوع", "Kind", ReportColumnTypes.Text),
                new("periodFrom", "من", "From", ReportColumnTypes.Date),
                new("periodTo", "إلى", "To", ReportColumnTypes.Date),
                new("holder", "أمين العهدة", "Custodian", ReportColumnTypes.Text),
                new("status", "الحالة", "Status", ReportColumnTypes.Text),
                new("funded", "ممّول", "Funded", ReportColumnTypes.Money, Total: true),
                new("distributed", "موزّع", "Distributed", ReportColumnTypes.Money, Total: true),
                new("paidFromBatch", "مدفوع من الدفعة", "Paid from batch", ReportColumnTypes.Money, Total: true),
                new("entitlementPaid", "أجور مدفوعة", "Wages paid", ReportColumnTypes.Money, Total: true),
                new("entitlementOutstanding", "أجور غير مدفوعة", "Wages unpaid", ReportColumnTypes.Money, Total: true),
                new("returned", "مرتجع", "Returned", ReportColumnTypes.Money, Total: true),
                new("remaining", "متبقٍ في الدفعة", "Remaining in batch", ReportColumnTypes.Money, Total: true),
                new("discrepancy", "فرق", "Discrepancy", ReportColumnTypes.Money, Total: true),
            ]),
        new(PartyPayments, "مدفوعات الموردين ومقاولي الباطن", "Supplier and subcontractor payments", ReportCategories.Finance, [new("payments", ReportActions.SeeMoney)],
            [new("from", ReportFilterTypes.Date, true, "من تاريخ", "From"), new("to", ReportFilterTypes.Date, true, "إلى تاريخ", "To"),
             new("payeeType", ReportFilterTypes.Option, false, "نوع المستفيد", "Beneficiary type", [PayeeTypes.Supplier, PayeeTypes.Subcontractor]), new("projectId", ReportFilterTypes.Id, false, "المشروع", "Project")],
            [
                new("payeeType", "نوع المستفيد", "Beneficiary type", ReportColumnTypes.Text),
                new("payee", "المستفيد", "Beneficiary", ReportColumnTypes.Text),
                new("projectId", "المشروع", "Project", ReportColumnTypes.Integer),
                new("payments", "عدد المدفوعات", "Payments", ReportColumnTypes.Integer, Total: true),
                new("fromTreasury", "من الخزينة", "From treasury", ReportColumnTypes.Money, Total: true),
                new("fromCustody", "من العهد", "From custodies", ReportColumnTypes.Money, Total: true),
                new("reversed", "معكوس", "Reversed", ReportColumnTypes.Money, Total: true),
                new("net", "الصافي", "Net paid", ReportColumnTypes.Money, Total: true),
            ]),
        new(Reconciliation, "العهد: الجرد والتسويات والمرتجعات", "Custody reconciliation: counts, adjustments, returns", ReportCategories.Finance, CustodyMoney,
            [new("from", ReportFilterTypes.Date, true, "من تاريخ", "From"), new("to", ReportFilterTypes.Date, true, "إلى تاريخ", "To")],
            [
                new("date", "التاريخ", "Date", ReportColumnTypes.Date),
                new("number", "الحساب", "Account", ReportColumnTypes.Text),
                new("kind", "النوع", "Kind", ReportColumnTypes.Text),
                new("detail", "البيان", "Detail", ReportColumnTypes.Text),
                new("bookBalance", "رصيد الدفاتر", "Book balance", ReportColumnTypes.Money),
                new("counted", "المعدود", "Counted", ReportColumnTypes.Money),
                new("difference", "الفرق", "Difference", ReportColumnTypes.Money, Total: true),
                new("adjustment", "تسوية", "Adjustment", ReportColumnTypes.Money, Total: true),
                new("returned", "مرتجع", "Returned", ReportColumnTypes.Money, Total: true),
                new("by", "بواسطة", "By", ReportColumnTypes.Text),
            ]),
        new(Activity, "سجل الحركات المالية (من بدأ ماذا)", "Financial activity (who initiated what)", ReportCategories.Finance, FinanceMoney,
            [new("from", ReportFilterTypes.Date, true, "من تاريخ", "From"), new("to", ReportFilterTypes.Date, true, "إلى تاريخ", "To"),
             new("actorUserId", ReportFilterTypes.Id, false, "المستخدم", "Initiated by"), new("kind", ReportFilterTypes.Option, false, "النوع", "Kind", ActivityKinds)],
            [
                new("date", "التاريخ", "Date", ReportColumnTypes.Date),
                new("kind", "النوع", "Kind", ReportColumnTypes.Text),
                new("number", "الرقم", "Number", ReportColumnTypes.Text),
                new("status", "الحالة", "Status", ReportColumnTypes.Text),
                new("amount", "المبلغ", "Amount", ReportColumnTypes.Money, Total: true),
                new("source", "من حساب", "Source", ReportColumnTypes.Text),
                new("destination", "إلى حساب / مستفيد", "Destination / payee", ReportColumnTypes.Text),
                new("projectId", "المشروع", "Project", ReportColumnTypes.Integer),
                new("initiatedBy", "بدأها", "Initiated by", ReportColumnTypes.Text),
                new("decidedBy", "قرّرها", "Decided by", ReportColumnTypes.Text),
                new("decision", "القرار / السبب", "Decision / reason", ReportColumnTypes.Text),
                new("evidence", "المستند", "Evidence", ReportColumnTypes.Text),
                new("reference", "المرجع", "Reference", ReportColumnTypes.Text),
            ]),
    ];

    public async Task<ReportData?> RunAsync(string code, ReportQuery query, ClaimsPrincipal caller, CancellationToken cancellationToken) => code switch
    {
        Treasury => await TreasuryAsync(cancellationToken),
        Custodians => await CustodiansAsync(query, cancellationToken),
        Payroll => await PayrollAsync(query, cancellationToken),
        PartyPayments => await PartyPaymentsAsync(query, cancellationToken),
        Reconciliation => await ReconciliationAsync(query, cancellationToken),
        Activity => await ActivityAsync(query, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a custody report."),
    };

    private async Task<ReportData> TreasuryAsync(CancellationToken cancellationToken)
    {
        var rows = new List<ReportRow>();
        foreach (var account in await db.TreasuryAccounts.AsNoTracking().OrderBy(account => account.Id).ToListAsync(cancellationToken))
        {
            var figures = await TreasuryQueries.FiguresAsync(db, account, cancellationToken);
            rows.Add(new ReportRow(new Dictionary<string, object?>
            {
                ["account"] = account.Account.ToString(),
                ["openingDate"] = figures.OpeningDate,
                ["openingBalance"] = figures.OpeningBalance,
                ["moneyOut"] = figures.MoneyOut,
                ["internalOut"] = figures.InternalOut,
                ["internalIn"] = figures.InternalIn,
                ["balance"] = figures.Balance,
                ["reserved"] = figures.Reserved,
                ["available"] = figures.Available,
            }));
        }

        return new ReportData(rows, "Balance = opening balance + every ledger entry of the account from the opening date. Custody advances and returns are internal transfers, never expenses.");
    }

    private async Task<ReportData> CustodiansAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var accounts = db.CustodyAccounts.AsNoTracking();
        if (query.Text("kind") is { } kind)
        {
            accounts = accounts.Where(account => account.Kind == kind);
        }

        if (query.Text("status") is { } status)
        {
            var wanted = Enum.Parse<CustodyAccountStatus>(status);
            accounts = accounts.Where(account => account.Status == wanted);
        }

        var list = await accounts.OrderBy(account => account.Kind).ThenBy(account => account.Id).ToListAsync(cancellationToken);
        var people = await users.FindAsync(list.Select(account => account.HolderUserId).OfType<int>().Distinct().ToList(), cancellationToken);
        var rows = new List<ReportRow>();
        foreach (var account in list)
        {
            var figures = await ledgers.FiguresAsync(account, cancellationToken);
            rows.Add(new ReportRow(new Dictionary<string, object?>
            {
                ["number"] = account.Number,
                ["kind"] = account.Kind,
                ["holder"] = account.HolderUserId is { } holder ? people.GetValueOrDefault(holder)?.NameEn ?? $"#{holder}" : null,
                ["name"] = account.NameEn,
                ["referenceId"] = account.ReferenceId,
                ["status"] = account.Status.ToString(),
                ["posted"] = figures.Posted,
                ["pending"] = figures.Pending,
                ["reserved"] = figures.Reserved,
                ["inTransit"] = figures.InTransit,
                ["available"] = figures.Available,
            }, new ReportLink("custodyAccount", account.Id)));
        }

        return new ReportData(rows, "Posted = the custody's ledger (Finance for accountants and payroll batches, Projects for engineers, Fleet for vehicles). Pending = claims and payroll not reviewed yet.");
    }

    private async Task<ReportData> PayrollAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var list = db.PayrollBatches.AsNoTracking().Include(batch => batch.Allocations);
        IQueryable<PayrollBatch> filtered = list;
        if (query.Text("status") is { } status)
        {
            var wanted = Enum.Parse<PayrollBatchStatus>(status);
            filtered = filtered.Where(batch => batch.Status == wanted);
        }

        var rows = new List<ReportRow>();
        foreach (var batch in await filtered.OrderByDescending(batch => batch.Id).ToListAsync(cancellationToken))
        {
            var view = await batches.ViewAsync(batch, cancellationToken);
            rows.Add(new ReportRow(new Dictionary<string, object?>
            {
                ["number"] = view.Number,
                ["kind"] = view.Kind,
                ["periodFrom"] = view.PeriodFrom,
                ["periodTo"] = view.PeriodTo,
                ["holder"] = view.Holder?.NameEn,
                ["status"] = view.Status,
                ["funded"] = view.Funded,
                ["distributed"] = view.Distributed,
                ["paidFromBatch"] = view.PaidFromBatch,
                ["entitlementPaid"] = view.EntitlementPaid,
                ["entitlementOutstanding"] = view.EntitlementOutstanding,
                ["returned"] = view.ReturnedToBatch + view.ReturnedToTreasury,
                ["remaining"] = view.BatchBalance,
                ["discrepancy"] = view.Discrepancy,
            }, new ReportLink("payrollBatch", view.Id)));
        }

        return new ReportData(rows, "Wages paid and unpaid come from Workforce (confirmed settlements) and HR (paid sheets): funding or distributing never marks anything paid.");
    }

    private async Task<ReportData> PartyPaymentsAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var from = query.Date("from")!.Value;
        var to = query.Date("to")!.Value;
        var types = query.Text("payeeType") is { } one ? new[] { one } : [PayeeTypes.Supplier, PayeeTypes.Subcontractor];
        var entries = db.LedgerEntries.AsNoTracking()
            .Where(entry => entry.EntryDate >= from && entry.EntryDate <= to && types.Contains(entry.CounterpartyType));
        if (query.Id("projectId") is { } projectId)
        {
            entries = entries.Where(entry => entry.ProjectId == projectId);
        }

        var list = await entries.Select(entry => new { entry.CounterpartyType, entry.CounterpartyId, entry.ProjectId, entry.Amount, entry.Account, entry.ReversesEntryId, entry.SourceType, entry.PaymentRequestId }).ToListAsync(cancellationToken);
        var names = new Dictionary<(string, int), PayeeName>();
        foreach (var group in list.GroupBy(entry => entry.SourceType))
        {
            if (sources.FirstOrDefault(source => source.SourceType == group.Key) is not { } source)
            {
                continue;
            }

            foreach (var (id, name) in await source.PayeeNamesAsync(group.Select(entry => entry.CounterpartyId).Distinct().ToList(), cancellationToken))
            {
                names[(group.Key, id)] = name;
            }
        }

        var rows = list.GroupBy(entry => (entry.CounterpartyType, entry.CounterpartyId, entry.ProjectId, entry.SourceType))
            .OrderBy(group => group.Key.CounterpartyType).ThenBy(group => group.Key.CounterpartyId).ThenBy(group => group.Key.ProjectId)
            .Select(group =>
            {
                var originals = group.Where(entry => entry.ReversesEntryId == null).ToList();
                var reversed = group.Where(entry => entry.ReversesEntryId != null).Sum(entry => entry.Amount);
                return new ReportRow(new Dictionary<string, object?>
                {
                    ["payeeType"] = group.Key.CounterpartyType,
                    ["payee"] = names.GetValueOrDefault((group.Key.SourceType, group.Key.CounterpartyId))?.NameEn ?? $"#{group.Key.CounterpartyId}",
                    ["projectId"] = group.Key.ProjectId,
                    ["payments"] = originals.Select(entry => entry.PaymentRequestId).Distinct().Count(),
                    ["fromTreasury"] = -originals.Where(entry => entry.Account != LedgerAccount.Custody).Sum(entry => entry.Amount),
                    ["fromCustody"] = -originals.Where(entry => entry.Account == LedgerAccount.Custody).Sum(entry => entry.Amount),
                    ["reversed"] = reversed,
                    ["net"] = -group.Sum(entry => entry.Amount),
                });
            })
            .ToList();

        return new ReportData(rows, "Every row reconciles to the ledger: a payment out of a custody is counted once, in \"From custodies\", never again at the treasury.");
    }

    private async Task<ReportData> ReconciliationAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var from = query.Date("from")!.Value;
        var to = query.Date("to")!.Value;
        var accounts = await db.CustodyAccounts.AsNoTracking().ToDictionaryAsync(account => account.Id, cancellationToken);
        var counts = await db.CustodyCashCounts.AsNoTracking().Where(count => count.CountDate >= from && count.CountDate <= to).ToListAsync(cancellationToken);
        var adjustments = await db.CustodyAdjustments.AsNoTracking().Where(adjustment => adjustment.EntryDate >= from && adjustment.EntryDate <= to).ToListAsync(cancellationToken);
        var returns = await db.CustodyTransfers.AsNoTracking()
            .Where(transfer => transfer.Purpose == PaymentPurposes.CustodyReturn && transfer.Status == CustodyTransferStatus.Received && transfer.ReceivedOn >= from && transfer.ReceivedOn <= to)
            .ToListAsync(cancellationToken);
        var people = await users.FindAsync(counts.Select(count => count.CountedBy).Concat(adjustments.Select(adjustment => adjustment.CreatedBy)).Concat(returns.Select(transfer => transfer.ReceivedBy ?? 0)).Where(id => id > 0).Distinct().ToList(), cancellationToken);
        string Number(int? accountId) => accountId is { } id && accounts.TryGetValue(id, out var account) ? account.Number : "Treasury";
        string Kind(int? accountId) => accountId is { } id && accounts.TryGetValue(id, out var account) ? account.Kind : "Treasury";
        string? Who(int? userId) => userId is { } id ? people.GetValueOrDefault(id)?.NameEn ?? $"#{id}" : null;

        var rows = counts.Select(count => (Date: count.CountDate, Row: new ReportRow(new Dictionary<string, object?>
            {
                ["date"] = count.CountDate, ["number"] = Number(count.AccountId), ["kind"] = Kind(count.AccountId), ["detail"] = "Cash count",
                ["bookBalance"] = count.BookBalance, ["counted"] = count.CountedAmount, ["difference"] = count.Difference, ["adjustment"] = 0m, ["returned"] = 0m, ["by"] = Who(count.CountedBy),
            }, new ReportLink("custodyAccount", count.AccountId))))
            .Concat(adjustments.Select(adjustment => (Date: adjustment.EntryDate, Row: new ReportRow(new Dictionary<string, object?>
            {
                ["date"] = adjustment.EntryDate, ["number"] = Number(adjustment.AccountId), ["kind"] = Kind(adjustment.AccountId), ["detail"] = $"Adjustment: {adjustment.Reason}",
                ["bookBalance"] = null, ["counted"] = null, ["difference"] = 0m, ["adjustment"] = adjustment.Amount, ["returned"] = 0m, ["by"] = Who(adjustment.CreatedBy),
            }, new ReportLink("custodyAccount", adjustment.AccountId)))))
            .Concat(returns.Select(transfer => (Date: transfer.ReceivedOn!.Value, Row: new ReportRow(new Dictionary<string, object?>
            {
                ["date"] = transfer.ReceivedOn, ["number"] = Number(transfer.SourceAccountId), ["kind"] = Kind(transfer.SourceAccountId), ["detail"] = $"Return {transfer.Number} to {Number(transfer.DestinationAccountId)}",
                ["bookBalance"] = null, ["counted"] = null, ["difference"] = 0m, ["adjustment"] = 0m, ["returned"] = transfer.Amount, ["by"] = Who(transfer.ReceivedBy),
            }, new ReportLink("custodyTransfer", transfer.Id)))))
            .OrderBy(row => row.Date)
            .Select(row => row.Row)
            .ToList();

        return new ReportData(rows, "A counted difference never changes a balance by itself: only a reviewed adjustment with a reason and evidence does, and it is listed here.");
    }

    /// <summary>
    /// One row per money step in the period: payment requests (who asked, who decided, how it was paid, the evidence
    /// reference), custody transfers, adjustments and cash counts. The Executive reads what happened from the records,
    /// not from anyone's explanation; the link opens the record.
    /// </summary>
    private async Task<ReportData> ActivityAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var from = query.Date("from")!.Value;
        var to = query.Date("to")!.Value;
        var actor = query.Id("actorUserId");
        var kinds = query.Text("kind") is { } one ? new[] { one } : ActivityKinds;
        var accounts = await db.CustodyAccounts.AsNoTracking().ToDictionaryAsync(account => account.Id, cancellationToken);
        string Number(int? accountId) => accountId is { } id && accounts.TryGetValue(id, out var account) ? account.Number : "Treasury";
        var rows = new List<(DateOnly Date, long Order, ReportRow Row)>();
        var userIds = new HashSet<int>();

        if (kinds.Contains("PaymentRequest"))
        {
            var requests = await db.PaymentRequests.AsNoTracking().Include(request => request.Events)
                .Where(request => request.RequestedDate >= from && request.RequestedDate <= to)
                .OrderBy(request => request.Id).ToListAsync(cancellationToken);
            var names = new Dictionary<(string, int), PayeeName>();
            foreach (var group in requests.GroupBy(request => request.SourceType))
            {
                if (sources.FirstOrDefault(source => source.SourceType == group.Key) is not { } source)
                {
                    continue;
                }

                foreach (var (id, name) in await source.PayeeNamesAsync(group.Select(request => request.PayeeId).Distinct().ToList(), cancellationToken))
                {
                    names[(group.Key, id)] = name;
                }
            }

            foreach (var request in requests)
            {
                var submitted = request.Events.OrderBy(step => step.Id).FirstOrDefault()?.ActorUserId;
                if (actor is { } wanted && submitted != wanted)
                {
                    continue;
                }

                foreach (var id in new[] { submitted, request.DecidedBy }.OfType<int>()) userIds.Add(id);
                var payee = names.TryGetValue((request.SourceType, request.PayeeId), out var name) ? name.NameEn : $"{request.PayeeType} #{request.PayeeId}";
                rows.Add((request.RequestedDate, request.Id, new ReportRow(new Dictionary<string, object?>
                {
                    ["date"] = request.RequestedDate,
                    ["kind"] = "PaymentRequest",
                    ["number"] = request.Number,
                    ["status"] = request.Status.ToString(),
                    ["amount"] = request.Amount,
                    ["source"] = request.SourceCustodyAccountId is { } custody ? Number(custody) : $"Treasury ({request.Method})",
                    ["destination"] = $"{request.PayeeType}: {payee}",
                    ["projectId"] = request.ProjectId,
                    ["initiatedBy"] = submitted,
                    ["decidedBy"] = request.DecidedBy,
                    ["decision"] = request.DecisionNote ?? request.CloseReason ?? request.FailureReason,
                    ["evidence"] = request.PaymentReference ?? request.BankReference,
                    ["reference"] = request.Reference,
                }, new ReportLink("payment-request", request.Id, request.ProjectId))));
            }
        }

        if (kinds.Contains("CustodyTransfer"))
        {
            var transfers = await db.CustodyTransfers.AsNoTracking()
                .Where(transfer => transfer.RequestedAt >= from.ToDateTime(TimeOnly.MinValue) && transfer.RequestedAt < to.AddDays(1).ToDateTime(TimeOnly.MinValue))
                .Where(transfer => actor == null || transfer.RequestedBy == actor)
                .OrderBy(transfer => transfer.Id).ToListAsync(cancellationToken);
            foreach (var transfer in transfers)
            {
                foreach (var id in new[] { (int?)transfer.RequestedBy, transfer.DecidedBy, transfer.DisbursedBy, transfer.ReceivedBy }.OfType<int>()) userIds.Add(id);
                rows.Add((DateOnly.FromDateTime(transfer.RequestedAt), transfer.Id, new ReportRow(new Dictionary<string, object?>
                {
                    ["date"] = DateOnly.FromDateTime(transfer.RequestedAt),
                    ["kind"] = "CustodyTransfer",
                    ["number"] = transfer.Number,
                    ["status"] = transfer.Status.ToString(),
                    ["amount"] = transfer.Amount,
                    ["source"] = transfer.SourceKind == TransferEndpointKind.Treasury ? $"Treasury ({transfer.SourceTreasuryAccount})" : Number(transfer.SourceAccountId),
                    ["destination"] = transfer.DestinationKind == TransferEndpointKind.Treasury ? $"Treasury ({transfer.DestinationTreasuryAccount})" : Number(transfer.DestinationAccountId),
                    ["projectId"] = transfer.ProjectId,
                    ["initiatedBy"] = transfer.RequestedBy,
                    ["decidedBy"] = transfer.DecidedBy,
                    ["decision"] = transfer.DecisionNote ?? transfer.CloseReason,
                    ["evidence"] = transfer.EvidenceReference ?? transfer.DisbursementReference,
                    ["reference"] = transfer.Reference,
                }, new ReportLink("custody-transfer", transfer.Id, transfer.ProjectId))));
            }
        }

        if (kinds.Contains("CustodyAdjustment"))
        {
            var adjustments = await db.CustodyAdjustments.AsNoTracking()
                .Where(adjustment => adjustment.EntryDate >= from && adjustment.EntryDate <= to && (actor == null || adjustment.CreatedBy == actor))
                .OrderBy(adjustment => adjustment.Id).ToListAsync(cancellationToken);
            foreach (var adjustment in adjustments)
            {
                userIds.Add(adjustment.CreatedBy);
                rows.Add((adjustment.EntryDate, adjustment.Id, new ReportRow(new Dictionary<string, object?>
                {
                    ["date"] = adjustment.EntryDate,
                    ["kind"] = "CustodyAdjustment",
                    ["number"] = $"ADJ-{adjustment.Id:000000}",
                    ["status"] = "Posted",
                    ["amount"] = adjustment.Amount,
                    ["source"] = adjustment.Amount < 0 ? Number(adjustment.AccountId) : null,
                    ["destination"] = adjustment.Amount > 0 ? Number(adjustment.AccountId) : null,
                    ["projectId"] = null,
                    ["initiatedBy"] = adjustment.CreatedBy,
                    ["decidedBy"] = adjustment.CreatedBy,
                    ["decision"] = adjustment.Reason,
                    ["evidence"] = adjustment.EvidenceReference,
                    ["reference"] = adjustment.CashCountId is { } count ? $"CNT-{count:000000}" : null,
                }, new ReportLink("custody-account", adjustment.AccountId))));
            }
        }

        if (kinds.Contains("CustodyCashCount"))
        {
            var counts = await db.CustodyCashCounts.AsNoTracking()
                .Where(count => count.CountDate >= from && count.CountDate <= to && (actor == null || count.CountedBy == actor))
                .OrderBy(count => count.Id).ToListAsync(cancellationToken);
            foreach (var count in counts)
            {
                userIds.Add(count.CountedBy);
                rows.Add((count.CountDate, count.Id, new ReportRow(new Dictionary<string, object?>
                {
                    ["date"] = count.CountDate,
                    ["kind"] = "CustodyCashCount",
                    ["number"] = $"CNT-{count.Id:000000}",
                    ["status"] = "Recorded",
                    ["amount"] = count.Difference,
                    ["source"] = Number(count.AccountId),
                    ["destination"] = null,
                    ["projectId"] = null,
                    ["initiatedBy"] = count.CountedBy,
                    ["decidedBy"] = null,
                    ["decision"] = count.Notes,
                    ["evidence"] = count.EvidenceReference,
                    ["reference"] = null,
                }, new ReportLink("custody-account", count.AccountId))));
            }
        }

        // Names in place of ids, once for the whole page.
        var people = await users.FindAsync(userIds.ToList(), cancellationToken);
        string? Who(object? userId) => userId is int id ? people.GetValueOrDefault(id)?.NameEn ?? $"#{id}" : null;
        var ordered = rows.OrderBy(row => row.Date).ThenBy(row => row.Order).Select(row =>
        {
            var cells = new Dictionary<string, object?>(row.Row.Cells)
            {
                ["initiatedBy"] = Who(row.Row.Cells["initiatedBy"]),
                ["decidedBy"] = Who(row.Row.Cells["decidedBy"]),
            };
            return new ReportRow(cells, row.Row.Link);
        }).ToList();

        return new ReportData(ordered, "Every row is a record in its own module with its history and evidence; the amount of a cash count is the counted difference, not a movement.");
    }
}
