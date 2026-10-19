namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// One alert to one person about one cost record of a project. An ordinary audited row: the Platform module turns its
/// insert into a stored notification (and a push) after the transaction commits, through the outbox, so an alert is
/// never sent for a save that rolled back, and a retried delivery never notifies twice.
/// </summary>
/// <remarks>
/// One table; the kind is the class (column <c>Kind</c>), so the notification rules see each kind as its own record,
/// like <see cref="LocationAlert"/>. One alert per record, kind and recipient (unique index), so a resubmitted record
/// does not notify the same person again.
/// </remarks>
internal abstract class CostAlert
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>The supply or expense the alert is about.</summary>
    public int ExpenseId { get; set; }
    public ProjectExpense Expense { get; set; } = null!;

    /// <summary>Who is told. Holds no amount or supplier name: the notification only points to the record.</summary>
    public int RecipientUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>A purchase was recorded above its reference price range: told to the project's manager and the Executives.</summary>
internal sealed class PriceAboveRangeAlert : CostAlert;

/// <summary>A site cost was recorded and waits for review: told to the project's manager.</summary>
internal sealed class CostRecordedAlert : CostAlert;
