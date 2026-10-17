using AlMotaheda.BuildingBlocks.Domain;
namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// The company's price-variance thresholds (one row, id 1). A supply whose unit price differs from its reference
/// price by more than the larger of the two allowances needs a review (docs/cost-flow §2.3).
/// </summary>
internal sealed class PricingPolicy
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Allowed difference as a percent of the reference price (0 = none).</summary>
    public decimal VarianceTolerancePercent { get; set; }

    /// <summary>Allowed difference per unit in EGP, for rounding (for example 0.01).</summary>
    public decimal RoundingAllowance { get; set; }

    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// A price variance requiring review: a supply bought at a unit price that differs from the approved reference
/// price of its catalog item on that day by more than the policy allows. It blames nobody; it asks someone with
/// catalog Approve (the Financial Manager or an Executive) to accept or reject the price for this one supply.
/// </summary>
/// <remarks>
/// <para>While <see cref="PriceVarianceStatus.Open"/> or <see cref="PriceVarianceStatus.Rejected"/>, the supply can't
/// be approved. Accepting never changes a catalog price. Editing the supply closes the variance as
/// <see cref="PriceVarianceStatus.Superseded"/> and the check runs again.</para>
/// <para>The figures are copied when the variance is raised, so later price changes never change them.</para>
/// </remarks>
internal sealed class PriceVariance
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int ExpenseId { get; set; }
    public ProjectExpense Expense { get; set; } = null!;

    public int CatalogItemId { get; set; }

    public decimal ReferencePrice { get; set; }

    public decimal ActualPrice { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>Actual − reference, per unit (negative when cheaper).</summary>
    public decimal UnitVariance { get; set; }

    /// <summary><see cref="UnitVariance"/> × quantity, to the piastre.</summary>
    public decimal TotalVariance { get; set; }

    /// <summary><see cref="UnitVariance"/> ÷ reference × 100, to two decimals.</summary>
    public decimal VariancePercent { get; set; }

    public VarianceDirection Direction { get; set; }

    /// <summary>The policy in force when the variance was raised.</summary>
    public decimal TolerancePercent { get; set; }

    public decimal RoundingAllowance { get; set; }

    private PriceVarianceStatus _status = PriceVarianceStatus.Open;

    /// <summary>Set when the record is created; afterwards only this class's methods change it.</summary>
    public PriceVarianceStatus Status { get => _status; init => _status = value; }

    public DateTime RaisedAt { get; set; }

    /// <summary>Who recorded or fixed the supply. They can never decide the variance.</summary>
    public int RaisedBy { get; set; }

    public DateTime? DecidedAt { get; set; }
    public int? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }

    public byte[] RowVersion { get; set; } = [];

    private static readonly StatusFlow<PriceVarianceStatus> Flow = new(
        (PriceVarianceStatus.Open, PriceVarianceStatus.Accepted),
        (PriceVarianceStatus.Open, PriceVarianceStatus.Rejected),
        (PriceVarianceStatus.Open, PriceVarianceStatus.Superseded),
        (PriceVarianceStatus.Rejected, PriceVarianceStatus.Superseded));

    /// <summary>Accept or reject the price of this one supply.</summary>
    public void Decide(PriceVarianceStatus outcome, int deciderId, DateTime now, string note)
    {
        if (outcome is not (PriceVarianceStatus.Accepted or PriceVarianceStatus.Rejected))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A decision accepts or rejects.");
        }

        _status = Flow.Move(_status, outcome);
        DecidedAt = now;
        DecidedBy = deciderId;
        DecisionNote = note;
    }

    /// <summary>The supply was edited, so this variance no longer describes it.</summary>
    public void Supersede(int userId, DateTime now)
    {
        _status = Flow.Move(_status, PriceVarianceStatus.Superseded);
        DecidedAt = now;
        DecidedBy = userId;
        DecisionNote = "The supply was changed; its price was checked again.";
    }
}

/// <summary>Stored by name.</summary>
internal enum PriceVarianceStatus
{
    /// <summary>Waiting for a decision. The supply can't be approved.</summary>
    Open,

    /// <summary>The price is accepted for this supply only. The supply can be approved.</summary>
    Accepted,

    /// <summary>The price is not accepted. The supply can't be approved until it is fixed or rejected.</summary>
    Rejected,

    /// <summary>The supply was changed after the variance was raised.</summary>
    Superseded,
}

/// <summary>Stored by name.</summary>
internal enum VarianceDirection
{
    Above,
    Below,
}
