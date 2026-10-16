namespace AlMotaheda.Modules.Finance.Contracts;

/// <summary>Who is paid. Stored by name.</summary>
public static class PayeeTypes
{
    public const string Supplier = "Supplier";
    public const string Worker = "Worker";
    public const string Subcontractor = "Subcontractor";

    public static IReadOnlyList<string> All { get; } = [Supplier, Worker, Subcontractor];
}

/// <summary>What a payment is for. Stored by name; reports group by it.</summary>
public static class PaymentPurposes
{
    public const string SupplierInvoices = "SupplierInvoices";
    public const string WorkerWages = "WorkerWages";
    public const string SubcontractWork = "SubcontractWork";
    public const string SubcontractAdvance = "SubcontractAdvance";
    public const string EquipmentCost = "EquipmentCost";

    public static IReadOnlyList<string> All { get; } = [SupplierInvoices, WorkerWages, SubcontractWork, SubcontractAdvance, EquipmentCost];
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

    public static IReadOnlyList<string> All { get; } = [SupplierPayment, WorkerPayment, SubcontractPayment, EquipmentCostPayment];
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
