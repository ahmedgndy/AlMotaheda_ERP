using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AlMotaheda.Modules.Projects.Data.Configurations;

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>
/// <c>POST /api/projects/{pid}/expenses</c>: record a supplier delivery (<c>Supply</c>) or a site expense
/// (<c>Expense</c>). It is saved as <c>Recorded</c>: a cost only once someone else approves it.
/// </summary>
/// <remarks>
/// <para>The server computes a supply's amount (quantity × unit price) and copies the approved catalog price of the
/// day when a catalog item is named, so a later price change never changes what was recorded.</para>
/// <para><b>Price check:</b> when a price list item is named (<c>priceItemId</c>), the server compares the unit price
/// with the reference range on the purchase date (the supplier's own range first) and copies the range and the result
/// onto the record. Above the range, the configured policy applies (<see cref="PriceCheckPolicy"/>): a justification
/// is required, or the record is refused; the project's manager and the Executives are alerted.</para>
/// <para><b>Retries:</b> with an <c>idempotencyKey</c>, sending the same save again returns the first record (200) and
/// records nothing; the same key from someone else or on another project answers 409 <c>idempotency_key_reused</c>.</para>
/// </remarks>
internal static class CreateExpense
{
    internal sealed record Request(
        string? Kind,
        string? ExpenseDate,
        int? SupplierId,
        string? ItemName,
        string? Unit,
        decimal? Quantity,
        decimal? UnitPrice,
        string? Category,
        string? Description,
        decimal? Amount,
        string? Channel,
        string? InvoiceNo,
        string? DeliveryNoteNo,
        int? CatalogItemId,
        int? ProjectItemId,
        string? Notes,
        int? PriceItemId = null,
        string? PriceJustification = null,
        string? IdempotencyKey = null) : IExpenseFieldsRequest;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/expenses", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Create)
            .WithName("CreateProjectExpense")
            .WithSummary("Record a supply or a site expense on a project (saved as Recorded, waiting for review)");

    internal static async Task<Results<Created<ExpenseView>, Ok<ExpenseView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CostAlerts alerts,
        IOptions<ProjectsOptions> options,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = ExpenseFields.Validate(request, clock.Today, errors);
        Guid? idempotencyKey = null;
        if (Input.Clean(request.IdempotencyKey) is { } keyText)
        {
            if (Guid.TryParse(keyText, out var key) && key != Guid.Empty)
            {
                idempotencyKey = key;
            }
            else
            {
                errors.Add("idempotencyKey", "idempotencyKey must be a GUID.");
            }
        }

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

        if (idempotencyKey is { } repeatKey
            && await RepeatAsync(db, users, projectId, caller, repeatKey, log, cancellationToken) is { } repeated)
        {
            return repeated;
        }

        var references = await ExpenseReferences.ResolveAsync(db, projectId, fields!, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (references.IsEquipmentCost)
        {
            return CoreProblems.UseEquipmentCosts();
        }

        if (references.ItemStatus is ProjectItemStatus.Inactive)
        {
            return WorkspaceProblems.InactiveItem();
        }

        var policy = options.Value.PriceCheckPolicy;
        var priceProblem = ExpensePricing.Check(fields!, references, policy, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (priceProblem is not null)
        {
            log.ActionRefused(caller.UserId, nameof(CreateExpense), "unit price above the reference range (policy Block)");
            return priceProblem;
        }

        var now = clock.UtcNow;
        var expense = new ProjectExpense
        {
            ProjectId = projectId,
            Status = ExpenseStatus.Recorded,
            CatalogPriceAtRecording = references.CatalogPrice,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        };
        fields!.ApplyTo(expense);
        fields.ApplyPriceCheckTo(expense, references.Price);

        if (fields.ProjectItemId is { } itemId)
        {
            expense.ProjectItemId = itemId;
            expense.ItemLinkedAt = now;
            expense.ItemLinkedBy = caller.UserId;
        }

        db.ProjectExpenses.Add(expense);

        // The alerts are saved with the record: the reviewer hears of a new cost, and a price above its range goes to
        // the manager and the Executives. Nobody is told about their own record.
        await alerts.AddAsync<CostRecordedAlert>(expense, await alerts.ManagersAsync(projectId, caller.UserId, cancellationToken), now, cancellationToken);
        var told = references.Price?.Comparison.IsAboveRange == true
            ? await alerts.AddAsync<PriceAboveRangeAlert>(
                expense, await alerts.ManagersAndExecutivesAsync(projectId, caller.UserId, cancellationToken), now, cancellationToken)
            : -1;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (idempotencyKey is { } raceKey && SqlViolations.IsUnique(exception, ProjectExpenseConfiguration.IdempotencyIndex))
        {
            // The same save arrived twice at once and the other one won: answer with its record.
            db.ChangeTracker.Clear();
            return await RepeatAsync(db, users, projectId, caller, raceKey, log, cancellationToken) ?? CoreProblems.KeyUsedElsewhere();
        }
        catch (DbUpdateException exception) when (expense.ProjectItemId is not null && SqlViolations.IsReferenceOrCheck(exception))
        {
            // The item was deleted by someone else after our check: a lost race is a 409, never a 500.
            return CoreProblems.ItemRemoved();
        }

        log.ExpenseRecorded(caller.UserId, projectId, expense.Id, expense.Kind.ToString(), expense.Channel.ToString());
        if (told >= 0)
        {
            log.PriceAboveRange(caller.UserId, projectId, expense.Id, policy.ToString(), told);
        }

        var view = await ExpenseView.LoadAsync(db, users, projectId, expense.Id, caller.Can(PermissionAction.SeeMoney), cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/expenses/{expense.Id}", view!);
    }

    /// <summary>
    /// The record an earlier save with this key created: 200 with it when it is the caller's on this project, 409 when
    /// the key belongs to someone else's record, <c>null</c> when the key is new.
    /// </summary>
    private static async Task<Results<Created<ExpenseView>, Ok<ExpenseView>, ValidationProblem, ProblemHttpResult>?> RepeatAsync(
        ProjectsDbContext db, IUserDirectory users, int projectId, ProjectCaller caller, Guid key, ProjectsLog log, CancellationToken cancellationToken)
    {
        var first = await db.ProjectExpenses.AsNoTracking()
            .Where(expense => expense.IdempotencyKey == key)
            .Select(expense => new { expense.Id, expense.ProjectId, expense.CreatedBy })
            .SingleOrDefaultAsync(cancellationToken);
        if (first is null)
        {
            return null;
        }

        if (first.ProjectId != projectId || first.CreatedBy != caller.UserId)
        {
            return CoreProblems.ExpenseKeyUsedElsewhere();
        }

        log.ExpenseRepeated(caller.UserId, projectId, first.Id);
        var view = await ExpenseView.LoadAsync(db, users, projectId, first.Id, caller.Can(PermissionAction.SeeMoney), cancellationToken);
        return TypedResults.Ok(view!);
    }
}
