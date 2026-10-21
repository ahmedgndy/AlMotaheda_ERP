using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Finance.Common;

/// <summary>Error responses of the Finance module, each with its machine <c>code</c>.</summary>
internal static class FinanceProblems
{
    public const string InvalidStatus = "invalid_status";
    public const string WrongMethod = "wrong_method";
    public const string OwnRecord = "own_record";
    public const string NotPayable = "not_payable";
    public const string BankRefused = "bank_refused";
    public const string Busy = "busy";
    public const string PermissionRevoked = "permission_revoked";
    public const string OpeningBalanceSet = "opening_balance_set";
    public const string CustodyAccountExists = "custody_account_exists";
    public const string CustodyAccountClosed = "custody_account_closed";
    public const string CustodyNotEmpty = "custody_not_empty";
    public const string CustodyInsufficient = "custody_insufficient";
    public const string CustodyBusy = "custody_busy";
    public const string TreasuryInsufficient = "treasury_insufficient";
    public const string SameAccount = "same_account";
    public const string OverDistribution = "over_distribution";
    public const string PeriodClosed = "period_closed";
    public const string PeriodBalanceNotZero = "period_balance_not_zero";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string BatchClosed = "batch_closed";
    public const string NotTheHolder = "not_the_holder";

    public static ValidationProblem Validation(ValidationErrors errors) => ApiProblem.Validation(errors.ToDictionary());

    public static ProblemHttpResult RequestNotFound() => ApiProblem.NotFoundResult("The payment request was not found.");

    public static ProblemHttpResult WrongStatus(string status) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, InvalidStatus, $"This can't be done to a request that is {status}.");

    public static ProblemHttpResult WrongPaymentMethod(string method) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, WrongMethod, $"This step doesn't apply to a {method} payment.");

    public static ProblemHttpResult OwnRequest() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, OwnRecord, "You created this request, so someone else must decide it.");

    public static ProblemHttpResult NotPayableAnymore(string reason) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, NotPayable, reason);

    public static ProblemHttpResult BankRefusedOrder(string? message) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, BankRefused, message ?? "The bank refused the payment order.");

    public static ProblemHttpResult TryAgainLater() =>
        ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, Busy, "Someone else is working on this request. Try again in a moment.");

    public static ProblemHttpResult TreasuryAccountNotFound() => ApiProblem.NotFoundResult("The treasury account was not found.");
    public static ProblemHttpResult CustodyAccountNotFound() => ApiProblem.NotFoundResult("The custody account was not found.");
    public static ProblemHttpResult TransferNotFound() => ApiProblem.NotFoundResult("The custody transfer was not found.");
    public static ProblemHttpResult BatchNotFound() => ApiProblem.NotFoundResult("The payroll batch was not found.");
    public static ProblemHttpResult CashCountNotFound() => ApiProblem.NotFoundResult("The cash count was not found.");

    public static ProblemHttpResult Conflict(string code, string title) => ApiProblem.Create(StatusCodes.Status409Conflict, code, title);

    public static ProblemHttpResult OpeningAlreadySet() =>
        Conflict(OpeningBalanceSet, "The opening balance of this account is already set. A correction is a ledger entry, never an edit.");

    public static ProblemHttpResult AccountExists() =>
        Conflict(CustodyAccountExists, "An active custody account of this kind already exists for this holder and reference.");

    public static ProblemHttpResult AccountClosed() =>
        Conflict(CustodyAccountClosed, "This custody account is closed. Nothing new can be recorded on it.");

    public static ProblemHttpResult AccountNotEmpty(decimal balance) =>
        Conflict(CustodyNotEmpty, $"The account still holds {balance:0.00}. Return or transfer the cash before closing it.");

    public static ProblemHttpResult NotEnoughCustody(decimal available, decimal amount) =>
        Conflict(CustodyInsufficient, $"The custody has {Math.Max(available, 0):0.00} available, less than {amount:0.00}.");

    public static ProblemHttpResult NotEnoughTreasury(decimal available, decimal amount) =>
        Conflict(TreasuryInsufficient, $"The treasury account has {Math.Max(available, 0):0.00} available, less than {amount:0.00}.");

    public static ProblemHttpResult CustodyIsBusy() =>
        ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, CustodyBusy, "This custody is being updated by someone else. Try again in a moment.");

    public static ProblemHttpResult SourceIsDestination() =>
        Conflict(SameAccount, "A transfer needs two different accounts.");

    public static ProblemHttpResult OverDistributed(decimal remaining, decimal amount) =>
        Conflict(OverDistribution, $"This would distribute {amount:0.00}, more than the {Math.Max(remaining, 0):0.00} still allowed for this project in the batch.");

    public static ProblemHttpResult ClosedPeriod(DateOnly periodEnd) =>
        Conflict(PeriodClosed, $"The period up to {periodEnd:yyyy-MM-dd} is closed on this account. Date the movement after it.");

    public static ProblemHttpResult BalanceNotZero(decimal balance) =>
        Conflict(PeriodBalanceNotZero, $"The policy requires a zero balance to close the period; the account holds {balance:0.00}.");

    public static ProblemHttpResult KeyUsedElsewhere() =>
        Conflict(IdempotencyKeyReused, "This form was already sent with different details. Reload and try again.");

    public static ProblemHttpResult ClosedBatch() =>
        Conflict(BatchClosed, "This payroll batch is closed.");

    public static ProblemHttpResult AdjustmentNotFound() => ApiProblem.NotFoundResult("The adjustment was not found.");

    public static ProblemHttpResult OnlyTheHolder(string what) =>
        ApiProblem.ForbiddenResult($"Only the holder of this custody account, or a custody reviewer, can {what}.");

    public static ProblemHttpResult ExecutiveOnly() =>
        ApiProblem.ForbiddenResult("Only a currently signed-in Executive can do this.");

    /// <summary>The token still says yes, but the database says the permission was removed or the sign-in ended.</summary>
    public static ProblemHttpResult Revoked() =>
        ApiProblem.Create(StatusCodes.Status403Forbidden, PermissionRevoked, "Your permission for this changed. Sign in again.");
}
