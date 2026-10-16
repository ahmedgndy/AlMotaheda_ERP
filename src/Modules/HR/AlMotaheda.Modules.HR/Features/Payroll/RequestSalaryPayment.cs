using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Features.Payroll;

/// <summary>
/// <c>POST /api/hr/salary-sheets/{id}/payment-request</c>: ask Finance to pay an approved salary sheet, for its total,
/// as one payment request (status Submitted) for the Executive. Needs payments Create and hr View (the Accountant).
/// The sheet becomes Paid only when Finance confirms the money left (<c>SalarySheetPaymentSource</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>409 <c>invalid_status</c> unless Approved; 409 <c>payment_requested</c> while an open request holds it. A
///   rejected or cancelled request frees it again.</item>
///   <item>Salaries are company overhead: the request and its ledger entries have no project (Needs Confirmation).</item>
///   <item>The same form sent again (same sheet and row version, same details) returns the first request with 200.</item>
/// </list>
/// </remarks>
internal static class RequestSalaryPayment
{
    internal sealed record Request(string? RequestedDate, string? Method, string? Reference, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/hr/salary-sheets/{sheetId:int}/payment-request", HandleAsync)
            .RequirePermission(HrPermissions.Payments, PermissionAction.Create)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("RequestSalaryPayment")
            .WithSummary("Ask Finance to pay an approved salary sheet (payments Create). 409 invalid_status, payment_requested");

    internal static async Task<Results<Created<PaymentRequestRef>, Ok<PaymentRequestRef>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int sheetId,
        Request request,
        HttpContext httpContext,
        HrDbContext db,
        IPaymentRequests requests,
        CompanyClock clock,
        HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var requestedDate = Input.RequiredDate(request.RequestedDate, errors, "requestedDate", "The payment date");
        string? method = null;
        if (Input.Clean(request.Method) is not { } methodText || !PaymentMethods.All.Contains(methodText))
        {
            errors.Add("method", $"method must be one of: {string.Join(", ", PaymentMethods.All)}.");
        }
        else
        {
            method = methodText;
        }

        var reference = Input.OptionalText(request.Reference, 100, errors, "reference", "The reference");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var key = PaymentKey(sheetId, rowVersion!);
        if (await FindRepeatAsync(requests, key, sheetId, requestedDate!.Value, method!, cancellationToken) is { } repeat)
        {
            return repeat;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Payroll, cancellationToken))
        {
            return HrProblems.TryAgainLater();
        }

        if (await FindRepeatAsync(requests, key, sheetId, requestedDate.Value, method!, cancellationToken) is { } committed)
        {
            return committed;
        }

        var sheet = await db.SalarySheets.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == sheetId, cancellationToken);
        if (sheet is null)
        {
            return HrProblems.SheetNotFound();
        }

        // Nothing is saved on the sheet now, so its row version is compared here: the form must show the current sheet.
        if (!sheet.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (sheet.Status != SalarySheetStatus.Approved)
        {
            return HrProblems.WrongStatus($"Only an approved salary sheet can be paid. This one is {sheet.Status}.");
        }

        var shared = SharedTransaction.Of(db);
        var held = await requests.HeldForPayeesAsync(PaymentSourceTypes.SalarySheetPayment, PayeeTypes.Employees, [sheetId], shared, cancellationToken);
        if (held.ContainsKey(sheetId))
        {
            return HrProblems.AlreadyRequested();
        }

        PaymentRequestRef created;
        try
        {
            created = await requests.SubmitAsync(
                new NewPaymentRequest(
                    PaymentSourceTypes.SalarySheetPayment,
                    PayeeTypes.Employees,
                    sheetId,
                    null,
                    PaymentPurposes.Salaries,
                    sheet.Total,
                    method!,
                    requestedDate.Value,
                    reference,
                    $"Salaries {PayrollMonths.Format(sheet.Month)}",
                    key,
                    caller.UserId,
                    []),
                shared,
                cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, PaymentRequestIndexes.IdempotencyKey))
        {
            await transaction.RollbackAsync(cancellationToken);
            return await FindRepeatAsync(requests, key, sheetId, requestedDate.Value, method!, cancellationToken) ?? HrProblems.KeyUsedElsewhere();
        }

        await transaction.CommitAsync(cancellationToken);
        log.SheetPaymentRequested(caller.UserId, sheetId, created.Id);

        return TypedResults.Created($"/api/payment-requests/{created.Id}", created);
    }

    /// <summary>The same sheet at the same row version is the same form, so a retry gets the same key.</summary>
    internal static Guid PaymentKey(int sheetId, byte[] rowVersion)
    {
        var hash = System.Security.Cryptography.SHA256.HashData([.. BitConverter.GetBytes(sheetId), .. rowVersion, .. "SalarySheetPayment"u8]);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static async Task<Results<Created<PaymentRequestRef>, Ok<PaymentRequestRef>, ValidationProblem, ProblemHttpResult>?> FindRepeatAsync(
        IPaymentRequests requests, Guid key, int sheetId, DateOnly requestedDate, string method, CancellationToken cancellationToken)
    {
        if (await requests.FindByKeyAsync(key, cancellationToken) is not { } existing)
        {
            return null;
        }

        if (existing.SourceType != PaymentSourceTypes.SalarySheetPayment || existing.PayeeId != sheetId
            || existing.RequestedDate != requestedDate || existing.Method != method)
        {
            return HrProblems.KeyUsedElsewhere();
        }

        return TypedResults.Ok(existing);
    }
}
