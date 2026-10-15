namespace AlMotaheda.BuildingBlocks.Data;

/// <summary>Formats values for a SQL <c>IN (...)</c> list inside a CHECK constraint, for example the names of an enum.</summary>
/// <remarks>Only for values that come from code (enum names, code lists), never from a request.</remarks>
public static class SqlList
{
    public static string Of(IEnumerable<string> values) => string.Join(", ", values.Select(value => $"'{value}'"));
}
