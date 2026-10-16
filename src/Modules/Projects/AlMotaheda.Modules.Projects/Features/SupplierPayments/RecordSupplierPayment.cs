using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.SupplierPayments;

/// <summary>
/// <c>POST /api/projects/{pid}/supplier-payments</c>: ask to pay a supplier, saying which approved credit records the
/// money settles. It creates a Finance payment request (status Submitted) for the Executive to decide; the payment
/// itself is written here only when the money has left (<c>SupplierPaymentSource</c>). Checked in one transaction
/// under the project lock, together with the request, so two requests can't both take the same invoice.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Needs payments Create and the project in scope. A completed project takes no payment except from an Executive.</item>
///   <item>At least one allocation, no record twice, every amount above zero, and the sum equal to the payment.</item>
///   <item>Every record: this project, this supplier, approved, on credit; and the allocation fits in what is still
///   unpaid and not already held by an open request.</item>
///   <item>The same <c>idempotencyKey</c> with the same content returns the first request with 200 and creates
///   nothing; the same key with anything different answers 409.</item>
/// </list>
/// </remarks>
internal static class RecordSupplierPayment
{
    internal sealed record AllocationLine(int? ExpenseId, decimal? Amount);

    internal sealed record Request(
        int? SupplierId,
        string? PaymentDate,
        decimal? Amount,
        string? Method,
        string? Reference,
        string? Notes,
        string? IdempotencyKey,
        IReadOnlyList<AllocationLine>? Allocations);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/supplier-payments", HandleAsync)
            .RequirePermission(PaymentPermissions.Module, PermissionAction.Create)
            .WithName("RecordSupplierPayment")
            .WithSummary("Request a payment to a supplier for approved credit records: creates a payment request for the Executive (idempotent by idempotencyKey)");

    internal static async Task<Results<Created<PaymentRequestRef>, Ok<PaymentRequestRef>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IPaymentRequests requests,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();

        if (request.SupplierId is null or < 1)
        {
            errors.Add("supplierId", "supplierId is required.");
        }

        var paymentDate = Input.RequiredDate(request.PaymentDate, errors, "paymentDate", "The payment date");
        if (paymentDate > clock.Today)
        {
            errors.Add("paymentDate", "The payment date can't be in the future.");
        }

        var amount = NumberInput.RequiredMoney(request.Amount, errors, "amount", "The amount");

        if (!Input.TryParseName<PaymentMethod>(request.Method, out var method))
        {
            errors.Add("method", $"method must be one of: {string.Join(", ", Enum.GetNames<PaymentMethod>())}.");
        }

        var reference = Input.OptionalText(request.Reference, SupplierPaymentConfiguration.ReferenceMaxLength, errors, "reference", "The reference");
        var notes = Input.OptionalText(request.Notes, SupplierPaymentConfiguration.NotesMaxLength, errors, "notes", "Notes");

        if (!Guid.TryParse(Input.Clean(request.IdempotencyKey), out var idempotencyKey) || idempotencyKey == Guid.Empty)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the client for this payment.");
        }

        var lines = PaymentAllocations.Validate(
            request.Allocations?.Select(line => (line.ExpenseId, line.Amount)).ToList(),
            amount,
            errors);

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return ProjectProblems.CompletedProject();
        }

        var supplierId = request.SupplierId!.Value;
        var sent = new PaymentContent(supplierId, paymentDate!.Value, amount!.Value, method, lines!.Select(line => (line.ExpenseId, line.Amount)).ToList());

        // A retried request: answer with the request it already made.
        if (await FindRepeatAsync(requests, projectId, idempotencyKey, sent, cancellationToken) is { } repeat)
        {
            return repeat;
        }

        if (!await db.Suppliers.AnyAsync(supplier => supplier.Id == supplierId, cancellationToken))
        {
            errors.Add("supplierId", "The supplier was not found.");
            return ProjectProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await ProjectLock.TryAcquireAsync(db, projectId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        // Checked again under the lock: a concurrent retry with the same key may have committed while this request
        // waited, and its request must be the answer (not an over-allocation error for money it already holds).
        if (await FindRepeatAsync(requests, projectId, idempotencyKey, sent, cancellationToken) is { } committed)
        {
            return committed;
        }

        var allocations = lines!;
        var expenseIds = allocations.Select(line => line.ExpenseId).ToArray();
        var targets = await ExpenseQueries.SettlementRows(db, projectId, expenseIds).ToListAsync(cancellationToken);

        foreach (var line in allocations)
        {
            var target = targets.FirstOrDefault(row => row.ExpenseId == line.ExpenseId);
            if (target is null)
            {
                return CoreProblems.ExpenseNotFound();
            }

            if (target.SupplierId != supplierId)
            {
                errors.Add("allocations", $"Record {line.ExpenseId} belongs to another supplier.");
                return ProjectProblems.Validation(errors);
            }

            if (!ExpenseRules.CanBePaid(target.Status, target.Channel))
            {
                return WorkspaceProblems.WrongStatus($"Record {line.ExpenseId} is not an approved credit record, so it can't be paid.");
            }
        }

        // What is still unpaid, less what open payment requests already hold, in this transaction.
        var shared = SharedTransaction.Of(db);
        var reserved = await requests.ReservedAsync(PaidRecordTypes.ProjectExpense, expenseIds, shared, cancellationToken);
        var remaining = targets.ToDictionary(
            row => row.ExpenseId,
            row => new AllocationTarget(row.ExpenseId, row.Amount, row.Allocated + reserved.GetValueOrDefault(row.ExpenseId)));

        if (PaymentAllocations.FindOverAllocation(allocations, remaining) is { } overpaid)
        {
            return CoreProblems.Overallocated(overpaid);
        }

        PaymentRequestRef created;
        try
        {
            created = await requests.SubmitAsync(
                new NewPaymentRequest(
                    PaymentSourceTypes.SupplierPayment,
                    PayeeTypes.Supplier,
                    supplierId,
                    projectId,
                    PaymentPurposes.SupplierInvoices,
                    amount!.Value,
                    method.ToString(),
                    paymentDate!.Value,
                    reference,
                    notes,
                    idempotencyKey,
                    caller.UserId,
                    allocations.Select(line => new PaymentLine(PaidRecordTypes.ProjectExpense, line.ExpenseId, projectId, line.Amount)).ToList()),
                shared,
                cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, PaymentRequestIndexes.IdempotencyKey))
        {
            // The same form was sent twice at the same moment: the other request won. Answer with its request.
            await transaction.RollbackAsync(cancellationToken);
            return await FindRepeatAsync(requests, projectId, idempotencyKey, sent, cancellationToken) ?? CoreProblems.KeyUsedElsewhere();
        }

        await transaction.CommitAsync(cancellationToken);
        log.SupplierPaymentRequested(caller.UserId, projectId, created.Id, supplierId, allocations.Count);

        return TypedResults.Created($"/api/payment-requests/{created.Id}", created);
    }

    private sealed record PaymentContent(int SupplierId, DateOnly PaymentDate, decimal Amount, PaymentMethod Method, IReadOnlyList<(int ExpenseId, decimal Amount)> Allocations);

    /// <summary>
    /// The answer for a key that was used before: the first payment (200) when it is on this project and asks for the
    /// same payment; 409 <c>idempotency_key_reused</c> when the key was used on another project or for a different
    /// payment (another supplier, amount, date, method or split); <c>null</c> when the key is new.
    /// </summary>
    private static async Task<Results<Created<PaymentRequestRef>, Ok<PaymentRequestRef>, ValidationProblem, ProblemHttpResult>?> FindRepeatAsync(
        IPaymentRequests requests, int projectId, Guid idempotencyKey, PaymentContent sent, CancellationToken cancellationToken)
    {
        if (await requests.FindByKeyAsync(idempotencyKey, cancellationToken) is not { } existing)
        {
            return null;
        }

        var sameSplit = existing.Lines.Count == sent.Allocations.Count
            && existing.Lines.All(line => line.RecordType == PaidRecordTypes.ProjectExpense
                                          && sent.Allocations.Any(other => other.ExpenseId == line.RecordId && other.Amount == line.Amount));
        if (existing.SourceType != PaymentSourceTypes.SupplierPayment
            || existing.ProjectId != projectId
            || existing.PayeeId != sent.SupplierId
            || existing.RequestedDate != sent.PaymentDate
            || existing.Amount != sent.Amount
            || existing.Method != sent.Method.ToString()
            || !sameSplit)
        {
            return CoreProblems.KeyUsedElsewhere();
        }

        return TypedResults.Ok(existing);
    }
}
