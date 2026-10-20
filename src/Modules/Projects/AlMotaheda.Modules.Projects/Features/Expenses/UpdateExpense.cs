using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Custody;
using AlMotaheda.Modules.Projects.Features.PriceControl;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>
/// <c>PUT /api/projects/{pid}/expenses/{id}</c>: the recorder fixes a record and resubmits it. Only while it is
/// <c>Recorded</c> or <c>Returned</c>; it goes back to <c>Recorded</c> and the review note is cleared. An approved
/// record is frozen: only its item link can change, through <c>PUT …/expenses/{id}/item</c>.
/// </summary>
internal static class UpdateExpense
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
        string? RowVersion,
        int? PriceItemId = null,
        string? PriceJustification = null,
        int? SupplyAgreementId = null,
        string? PayeeName = null) : IExpenseFieldsRequest;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPut("/{projectId:int}/expenses/{expenseId:int}", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Create)
            .WithName("UpdateProjectExpense")
            .WithSummary("Fix and resubmit a recorded or returned record (the recorder only)");

    internal static async Task<Results<Ok<ExpenseView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int expenseId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CostAlerts alerts,
        PriceExceptionCases cases,
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
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
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

        var expense = await db.ProjectExpenses
            .SingleOrDefaultAsync(candidate => candidate.Id == expenseId && candidate.ProjectId == projectId, cancellationToken);
        if (expense is null)
        {
            return CoreProblems.ExpenseNotFound();
        }

        switch (ExpenseRules.Resubmit(expense.Status, expense.CreatedBy, caller.UserId))
        {
            case ExpenseDecision.NotRecorder:
                log.ActionRefused(caller.UserId, nameof(UpdateExpense), "not the recorder");
                return CoreProblems.NotTheRecorder();
            case ExpenseDecision.WrongStatus:
                return WorkspaceProblems.WrongStatus("Only a recorded or returned record can be changed. An approved record is frozen.");
        }

        // A supply under an agreement takes the agreement's work item (and must match its supplier and unit).
        var (withAgreement, agreementProblem) = await ExpenseAgreements.ApplyAsync(db, projectId, fields!, errors, cancellationToken, keptAgreementId: expense.SupplyAgreementId);
        fields = withAgreement;
        if (agreementProblem is not null && !errors.HasErrors)
        {
            return agreementProblem;
        }

        var references = await ExpenseReferences.ResolveAsync(db, projectId, fields, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (references.IsEquipmentCost)
        {
            return CoreProblems.UseEquipmentCosts();
        }

        // Linking to an inactive item is refused, unless the record is already on it (nothing changes then).
        if (references.ItemStatus is ProjectItemStatus.Inactive && fields!.ProjectItemId != expense.ProjectItemId)
        {
            return WorkspaceProblems.InactiveItem();
        }

        // The price is checked again with the corrected figures and the range on the (possibly new) date.
        var policy = options.Value.PriceCheckPolicy;
        var priceProblem = ExpensePricing.Check(fields!, references, policy, errors, keptPriceItemId: expense.PriceItemId);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (priceProblem is not null)
        {
            return priceProblem;
        }

        // Paid from the recorder's custody: the corrected amount is checked against the custody (its old amount left out).
        var custody = fields!.Channel == PaymentChannel.Custody
            ? await CustodySpendCheck.CheckAsync(db, projectId, caller.UserId, fields.Amount, expense.Id, options.Value.BlocksCustodyOverdraft, cancellationToken)
            : CustodySpend.None;
        if (custody.Problem is not null)
        {
            log.ActionRefused(caller.UserId, nameof(UpdateExpense), "more than the custody holds (policy Block)");
            return custody.Problem;
        }

        await using var transaction = custody.Transaction ?? await db.Database.BeginTransactionAsync(cancellationToken);

        var now = clock.UtcNow;
        var oldStatus = expense.Status;

        fields!.ApplyTo(expense);
        fields.ApplyPriceCheckTo(expense, references.Price);
        expense.CatalogPriceAtRecording = references.CatalogPrice;
        expense.Resubmit(caller.UserId, now);

        if (fields.ProjectItemId != expense.ProjectItemId)
        {
            expense.ProjectItemId = fields.ProjectItemId;
            expense.ItemLinkedAt = fields.ProjectItemId is null ? null : now;
            expense.ItemLinkedBy = fields.ProjectItemId is null ? null : caller.UserId;
        }

        db.Entry(expense).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (expense.ProjectItemId is not null && SqlViolations.IsReferenceOrCheck(exception))
        {
            // The item was deleted by someone else after our check: a lost race is a 409, never a 500.
            return CoreProblems.ItemRemoved();
        }

        // The corrected record starts clean: its open case is withdrawn, and a price still above the range opens a
        // fresh case for the Executive (a rejected price resubmitted unchanged is asked again, with its reason).
        await cases.WithdrawOpenAsync(PriceExceptionSources.ProjectExpense, expense.Id, caller.UserId, cancellationToken);
        if (references.Price is { Comparison.IsAboveRange: true } above)
        {
            var exception = await cases.OpenAsync(
                PriceExceptionSources.ProjectExpense, expense.Id, projectId, expense.SupplierId, above, ExpenseLabel.Of(expense), expense.ExpenseDate,
                caller.UserId, cancellationToken);
            exception.Reason = fields.PriceJustification;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        log.ExpenseResubmitted(caller.UserId, projectId, expense.Id, oldStatus.ToString());
        if (custody.Overdrawn)
        {
            log.CustodyOverdrawn(caller.UserId, projectId, expense.Id);
        }


        var view = await ExpenseView.LoadAsync(db, users, projectId, expense.Id, caller.Can(PermissionAction.SeeMoney), cancellationToken);
        return view is null ? CoreProblems.ExpenseNotFound() : TypedResults.Ok(view);
    }
}
