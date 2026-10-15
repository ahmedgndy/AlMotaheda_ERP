using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Common;
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
/// <c>POST /api/projects/{pid}/supplier-payments</c>: record money paid to a supplier and say which approved
/// credit records it settles. Checked in one transaction under the project lock, so two payments can't both
/// settle the same invoice.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Needs payments Create and the project in scope. A completed project takes no payment except from an Executive.</item>
///   <item>At least one allocation, no record twice, every amount above zero, and the sum equal to the payment.</item>
///   <item>Every record: this project, this supplier, approved, on credit; and the allocation fits in what is still unpaid.</item>
///   <item>The same <c>idempotencyKey</c> again returns the first payment with 200 and records nothing; the same key
///   on another project answers 409.</item>
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
            .WithSummary("Record a payment to a supplier and allocate it to approved credit records (idempotent by idempotencyKey)");

    internal static async Task<Results<Created<SupplierPaymentView>, Ok<SupplierPaymentView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
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

        // A retried request: answer with the payment it already made.
        if (await FindRepeatAsync(db, projectId, idempotencyKey, sent, cancellationToken) is { } repeat)
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
        // waited, and its payment must be the answer (not an over-allocation error for money already settled).
        if (await FindRepeatAsync(db, projectId, idempotencyKey, sent, cancellationToken) is { } committed)
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

        var remaining = targets.ToDictionary(
            row => row.ExpenseId,
            row => new AllocationTarget(row.ExpenseId, row.Amount, row.Allocated));

        if (PaymentAllocations.FindOverAllocation(allocations, remaining) is { } overpaid)
        {
            return CoreProblems.Overallocated(overpaid);
        }

        var payment = new SupplierPayment
        {
            ProjectId = projectId,
            SupplierId = supplierId,
            PaymentDate = paymentDate!.Value,
            Amount = amount!.Value,
            Method = method,
            Reference = reference,
            Notes = notes,
            IdempotencyKey = idempotencyKey,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        payment.Allocations.AddRange(allocations.Select(line => new SupplierPaymentAllocation { ExpenseId = line.ExpenseId, Amount = line.Amount }));
        db.SupplierPayments.Add(payment);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SupplierPaymentConfiguration.IdempotencyKeyIndex))
        {
            // The same form was sent twice at the same moment: the other request won. Answer with its payment.
            await transaction.RollbackAsync(cancellationToken);
            return await FindRepeatAsync(db, projectId, idempotencyKey, sent, cancellationToken) ?? CoreProblems.KeyUsedElsewhere();
        }

        await transaction.CommitAsync(cancellationToken);
        log.SupplierPaymentRecorded(caller.UserId, projectId, payment.Id, supplierId, payment.Allocations.Count);

        var view = await PaymentQueries.One(db, projectId, payment.Id).SingleAsync(cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/supplier-payments/{payment.Id}", SupplierPaymentView.From(view));
    }

    /// <summary>What a payment request asks for, to tell a retry from a different payment sent with the same key.</summary>
    private sealed record PaymentContent(int SupplierId, DateOnly PaymentDate, decimal Amount, PaymentMethod Method, IReadOnlyList<(int ExpenseId, decimal Amount)> Allocations);

    /// <summary>
    /// The answer for a key that was used before: the first payment (200) when it is on this project and asks for the
    /// same payment; 409 <c>idempotency_key_reused</c> when the key was used on another project or for a different
    /// payment (another supplier, amount, date, method or split); <c>null</c> when the key is new.
    /// </summary>
    private static async Task<Results<Created<SupplierPaymentView>, Ok<SupplierPaymentView>, ValidationProblem, ProblemHttpResult>?> FindRepeatAsync(
        ProjectsDbContext db, int projectId, Guid idempotencyKey, PaymentContent sent, CancellationToken cancellationToken)
    {
        var existing = await PaymentQueries.ByKey(db, idempotencyKey)
            .Select(payment => new
            {
                payment.Id,
                payment.ProjectId,
                payment.SupplierId,
                payment.PaymentDate,
                payment.Amount,
                payment.Method,
                Allocations = payment.Allocations.Select(line => new { line.ExpenseId, line.Amount }).ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            return null;
        }

        var sameSplit = existing.Allocations.Count == sent.Allocations.Count
            && existing.Allocations.All(line => sent.Allocations.Any(other => other.ExpenseId == line.ExpenseId && other.Amount == line.Amount));
        if (existing.ProjectId != projectId
            || existing.SupplierId != sent.SupplierId
            || existing.PaymentDate != sent.PaymentDate
            || existing.Amount != sent.Amount
            || existing.Method != sent.Method
            || !sameSplit)
        {
            return CoreProblems.KeyUsedElsewhere();
        }

        var view = await PaymentQueries.One(db, projectId, existing.Id).SingleAsync(cancellationToken);
        return TypedResults.Ok(SupplierPaymentView.From(view));
    }
}
