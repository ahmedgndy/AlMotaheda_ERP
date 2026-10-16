namespace AlMotaheda.BuildingBlocks.Domain;

/// <summary>
/// The status moves a record allows, written once next to the record. A workflow entity changes its status only
/// through its own methods, and every method asks its flow first: a move that isn't listed throws, because it means
/// a bug (the endpoints check their business rules before, and answer 409 for a wrong status).
/// </summary>
/// <example>
/// <code>
/// private static readonly StatusFlow&lt;ExpenseStatus&gt; Flow = new(
///     (ExpenseStatus.Recorded, ExpenseStatus.Approved),
///     (ExpenseStatus.Returned, ExpenseStatus.Recorded));
/// </code>
/// </example>
public sealed class StatusFlow<TStatus>
    where TStatus : struct, Enum
{
    private readonly HashSet<(TStatus From, TStatus To)> _moves;

    public StatusFlow(params (TStatus From, TStatus To)[] moves) => _moves = [.. moves];

    public bool Allows(TStatus from, TStatus to) => _moves.Contains((from, to));

    /// <summary>Returns <paramref name="to"/> when the move is listed; throws otherwise.</summary>
    public TStatus Move(TStatus from, TStatus to) =>
        Allows(from, to)
            ? to
            : throw new InvalidOperationException($"A {typeof(TStatus).Name} can't move from {from} to {to}.");
}
