using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>Error responses shared by the project workspace features (items, expenses, budgets, reports).</summary>
internal static class WorkspaceProblems
{
    public const string OwnRecord = "own_record";
    public const string InvalidStatus = "invalid_status";
    public const string ItemInactive = "item_inactive";
    public const string IdempotencyKeyReused = "idempotency_key_reused";

    public static ProblemHttpResult NotFound(string what) => ApiProblem.NotFoundResult($"The {what} was not found.");

    /// <summary>The caller recorded this record, so someone else must review it.</summary>
    public static ProblemHttpResult ReviewerIsRecorder() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, OwnRecord, "You can't review a record you recorded. Another reviewer must do it.");

    /// <summary>The record is not in a status that allows this action.</summary>
    public static ProblemHttpResult WrongStatus(string title) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, InvalidStatus, title);

    public static ProblemHttpResult InactiveItem() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ItemInactive, "This project item is inactive. New records can't be linked to it.");

    public static ProblemHttpResult Conflict(string code, string title) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, code, title);
}

/// <summary>Recognises SQL Server constraint failures, so a lost race becomes a 409 instead of a 500.</summary>
internal static class SqlViolations
{
    private const int DuplicateKeyInUniqueIndex = 2601;
    private const int DuplicateKeyInUniqueConstraint = 2627;
    private const int ForeignKeyConflict = 547;

    /// <summary>True when the save failed on the unique index called <paramref name="indexName"/>.</summary>
    public static bool IsUnique(DbUpdateException exception, string indexName) =>
        exception.InnerException is SqlException { Number: DuplicateKeyInUniqueIndex or DuplicateKeyInUniqueConstraint } sql
        && sql.Message.Contains(indexName, StringComparison.Ordinal);

    /// <summary>True when the save failed because another row still points to this one (or a check failed).</summary>
    public static bool IsReferenceOrCheck(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: ForeignKeyConflict };
}

/// <summary>The <c>page</c> and <c>pageSize</c> parameters every list takes.</summary>
internal static class Paging
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxPage = 100_000;

    public static void Validate(int page, int pageSize, ValidationErrors errors)
    {
        if (page is < 1 or > MaxPage)
        {
            errors.Add("page", $"page must be between 1 and {MaxPage}.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors.Add("pageSize", $"pageSize must be between 1 and {MaxPageSize}.");
        }
    }
}

/// <summary>Input rules for numbers, added to <see cref="Input"/>'s rules for text and dates.</summary>
internal static class NumberInput
{
    /// <summary>A required amount of money greater than zero, with at most two decimals.</summary>
    public static decimal? RequiredMoney(decimal? value, ValidationErrors errors, string field, string label)
    {
        if (value is null)
        {
            errors.Add(field, $"{label} is required.");
            return null;
        }

        return Money(value.Value, errors, field, label);
    }

    /// <summary>An optional amount of money greater than zero, with at most two decimals.</summary>
    public static decimal? OptionalMoney(decimal? value, ValidationErrors errors, string field, string label) =>
        value is null ? null : Money(value.Value, errors, field, label);

    /// <summary>A required quantity greater than zero, with at most three decimals.</summary>
    public static decimal? RequiredQuantity(decimal? value, ValidationErrors errors, string field, string label)
    {
        if (value is null)
        {
            errors.Add(field, $"{label} is required.");
            return null;
        }

        if (value <= 0)
        {
            errors.Add(field, $"{label} must be greater than zero.");
            return null;
        }

        if (value > MaxQuantity || decimal.Round(value.Value, 3) != value.Value)
        {
            errors.Add(field, $"{label} must have at most three decimals and be at most {MaxQuantity:0}.");
            return null;
        }

        return value;
    }

    /// <summary>The largest amount accepted in one field. Far above any real invoice; stops overflow of DECIMAL(18,2) sums.</summary>
    public const decimal MaxMoney = 9_999_999_999.99m;

    public const decimal MaxQuantity = 999_999_999.999m;

    private static decimal? Money(decimal value, ValidationErrors errors, string field, string label)
    {
        if (value <= 0)
        {
            errors.Add(field, $"{label} must be greater than zero.");
            return null;
        }

        if (value > MaxMoney || decimal.Round(value, 2) != value)
        {
            errors.Add(field, $"{label} must have at most two decimals and be at most {MaxMoney:0.00}.");
            return null;
        }

        return value;
    }
}
