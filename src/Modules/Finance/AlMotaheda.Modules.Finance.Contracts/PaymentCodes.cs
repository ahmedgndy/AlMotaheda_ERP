namespace AlMotaheda.Modules.Finance.Contracts;

/// <summary>Who is paid. Stored by name.</summary>
public static class PayeeTypes
{
    public const string Supplier = "Supplier";
    public const string Worker = "Worker";
    public const string Subcontractor = "Subcontractor";

    /// <summary>The employees of one HR salary sheet, paid as one batch; the payee id is the sheet's id.</summary>
    public const string Employees = "Employees";

    /// <summary>A person given cash to spend on a project (a site engineer's custody, "العهدة"); the payee id is the user id.</summary>
    public const string CustodyHolder = "CustodyHolder";

    /// <summary>
    /// A custody account of the company's treasury hierarchy (<c>finance.CustodyAccount</c>): the counterparty of an
    /// internal transfer (money handed to the accountant, a payroll batch, a vehicle), never a payee of an expense.
    /// </summary>
    public const string CustodyAccount = "CustodyAccount";

    public static IReadOnlyList<string> All { get; } = [Supplier, Worker, Subcontractor, Employees, CustodyHolder, CustodyAccount];
}

/// <summary>What a payment is for. Stored by name; reports group by it.</summary>
public static class PaymentPurposes
{
    public const string SupplierInvoices = "SupplierInvoices";
    public const string WorkerWages = "WorkerWages";
    public const string SubcontractWork = "SubcontractWork";
    public const string SubcontractAdvance = "SubcontractAdvance";
    public const string EquipmentCost = "EquipmentCost";

    /// <summary>A monthly salary sheet of office employees, engineers and staff (HR). Company overhead: no project.</summary>
    public const string Salaries = "Salaries";

    /// <summary>
    /// Cash handed to a person's custody for a project. Money out of the company, but not a project cost: the cost is
    /// recognized from the purchases and wages paid out of the custody, each once.
    /// </summary>
    public const string CustodyAdvance = "CustodyAdvance";

    /// <summary>Cash handed back from a custody to the treasury. Money in, never income: it undoes an advance.</summary>
    public const string CustodyReturn = "CustodyReturn";

    public static IReadOnlyList<string> All { get; } = [SupplierInvoices, WorkerWages, SubcontractWork, SubcontractAdvance, EquipmentCost, Salaries, CustodyAdvance, CustodyReturn];

    /// <summary>An internal movement between the treasury and a custody, or between custodies: counted zero times as expense.</summary>
    public static bool IsInternalTransfer(string purpose) => purpose is CustodyAdvance or CustodyReturn;
}

/// <summary>How money leaves. Cash is paid by hand; bank transfers and cheques go through the bank and wait for its confirmation.</summary>
public static class PaymentMethods
{
    public const string BankTransfer = "BankTransfer";
    public const string Cheque = "Cheque";
    public const string Cash = "Cash";

    public static IReadOnlyList<string> All { get; } = [BankTransfer, Cheque, Cash];

    public static bool GoesThroughBank(string method) => method is BankTransfer or Cheque;
}

/// <summary>The payment kinds a source module settles (<see cref="IPaymentSource.SourceType"/>).</summary>
public static class PaymentSourceTypes
{
    public const string SupplierPayment = "SupplierPayment";
    public const string WorkerPayment = "WorkerPayment";
    public const string SubcontractPayment = "SubcontractPayment";
    public const string EquipmentCostPayment = "EquipmentCostPayment";
    public const string SalarySheetPayment = "SalarySheetPayment";

    /// <summary>Cash handed to a custody (Projects writes the custody funding when the request is paid).</summary>
    public const string CustodyFunding = "CustodyFunding";

    /// <summary>
    /// An internal transfer between the treasury and the custody hierarchy, written by Finance itself. Only a ledger
    /// entry's <c>SourceType</c>: a transfer is never a payment request.
    /// </summary>
    public const string CustodyTransfer = "CustodyTransfer";

    /// <summary>The source types a payment request may have (a custody transfer is not one).</summary>
    public static IReadOnlyList<string> All { get; } = [SupplierPayment, WorkerPayment, SubcontractPayment, EquipmentCostPayment, SalarySheetPayment, CustodyFunding];
}

/// <summary>The records a request's lines point to (what each part of the money pays for).</summary>
public static class PaidRecordTypes
{
    public const string ProjectExpense = "ProjectExpense";
    public const string AttendanceLine = "AttendanceLine";
    public const string SubContract = "SubContract";
    public const string EquipmentCost = "EquipmentCost";
}

/// <summary>The request statuses. Stored by name and returned to the client.</summary>
public static class PaymentRequestStatuses
{
    public const string Submitted = "Submitted";
    public const string Approved = "Approved";
    public const string SentToBank = "SentToBank";
    public const string Paid = "Paid";
    public const string Rejected = "Rejected";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Reversed = "Reversed";
}

/// <summary>Database names the source modules recognise when two identical forms race (a lost race becomes the first answer).</summary>
public static class PaymentRequestIndexes
{
    public const string IdempotencyKey = "UX_PaymentRequest_IdempotencyKey";
}
