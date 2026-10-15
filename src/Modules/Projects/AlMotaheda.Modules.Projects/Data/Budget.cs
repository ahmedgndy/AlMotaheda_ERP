namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// One version of a project's cost estimate ("الموازنة التقديرية"): what the project plans to spend, line by line.
/// A budget is a plan, never a cost: nothing here is counted as money spent.
/// </summary>
/// <remarks>
/// <para><b>Versions are never overwritten.</b> A revision is a new row with the next <see cref="Version"/>.
/// Approving a version makes the previous approved one <see cref="BudgetStatus.Superseded"/>, so a comparison made
/// against an old version stays true.</para>
/// <para>Only a <see cref="BudgetStatus.Draft"/> can be edited. A project has at most one draft and at most one
/// approved budget at a time (two filtered unique indexes).</para>
/// <para>The budget holds no total column: totals are sums of its <see cref="Lines"/>.</para>
/// </remarks>
internal sealed class Budget
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>1, 2, 3… within the project, in the order the versions were created.</summary>
    public int Version { get; set; }

    public BudgetStatus Status { get; set; } = BudgetStatus.Draft;

    public string? Title { get; set; }
    public string? Notes { get; set; }

    /// <summary>Set, with <see cref="ApprovedBy"/>, when the version is approved. Kept when it is superseded.</summary>
    public DateTime? ApprovedAt { get; set; }
    public int? ApprovedBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<BudgetLine> Lines { get; } = [];
}

/// <summary>Stored by name.</summary>
internal enum BudgetStatus
{
    /// <summary>Being prepared. The only status that can be edited.</summary>
    Draft,

    /// <summary>The budget in force. Never changes again.</summary>
    Approved,

    /// <summary>Was approved, then replaced by a newer approved version. Kept for history.</summary>
    Superseded,
}

/// <summary>
/// One line of a budget: an estimated amount in one cost group, optionally for one project item.
/// </summary>
/// <remarks>
/// A line is either priced (<see cref="Quantity"/> and <see cref="UnitPrice"/> both set, and the server sets
/// <see cref="Amount"/> to their rounded product) or a lump sum (both empty, and the amount is the estimate itself).
/// </remarks>
internal sealed class BudgetLine
{
    public int Id { get; set; }

    public int BudgetId { get; set; }
    public Budget Budget { get; set; } = null!;

    /// <summary>1, 2, 3… within the budget: the order the lines are shown in.</summary>
    public int LineNo { get; set; }

    /// <summary>One of <c>CostGroups</c> (Projects.Contracts).</summary>
    public required string CostGroup { get; set; }

    /// <summary>Optional: the project item this estimate is for. Same project as the budget.</summary>
    public int? ProjectItemId { get; set; }
    public ProjectItem? ProjectItem { get; set; }

    public required string Description { get; set; }

    /// <summary>One of <c>MeasurementUnits</c>, or empty.</summary>
    public string? Unit { get; set; }

    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }

    /// <summary>The estimated amount in EGP. Always greater than zero.</summary>
    public decimal Amount { get; set; }
}
