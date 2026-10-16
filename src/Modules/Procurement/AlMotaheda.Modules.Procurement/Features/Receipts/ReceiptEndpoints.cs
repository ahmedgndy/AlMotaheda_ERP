using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Procurement.Common;
using AlMotaheda.Modules.Procurement.Data;
using AlMotaheda.Modules.Procurement.Data.Configurations;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Procurement.Features.Receipts;

/// <summary>
/// <c>POST /api/procurement/purchase-orders/{id}/receipts</c> <c>{ receivedOn, deliveryNoteNo?, notes?, lines: [{ orderLineId, quantity }] }</c>:
/// the goods receipt note, recorded on site by a member of the project's team with site Create (or an Executive).
/// Purchasing can't record receipts. Partial deliveries are normal; more than is still due is refused.
/// <c>GET /api/procurement/goods-receipts/{id}</c> (procurement View).
/// </summary>
internal static class ReceiptEndpoints
{
    internal sealed record LineRequest(int? OrderLineId, decimal? Quantity);

    internal sealed record Request(string? ReceivedOn, string? DeliveryNoteNo, string? Notes, IReadOnlyList<LineRequest>? Lines);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/procurement/purchase-orders/{orderId:int}/receipts", RecordAsync)
            .RequirePermission(ProcurementPermissions.Site, PermissionAction.Create)
            .WithName("RecordGoodsReceipt")
            .WithSummary("Record goods received on site (site Create, project team). 409 exceeds_ordered, invalid_status");

        api.MapGet("/procurement/goods-receipts/{receiptId:int}", GetAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.View)
            .WithName("GetGoodsReceipt")
            .WithSummary("Get one goods receipt note");
    }

    internal static async Task<Results<Created<ReceiptView>, ValidationProblem, ProblemHttpResult>> RecordAsync(
        int orderId, Request request, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, ProcurementViews views,
        CompanyClock clock, ProcurementLog log, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var receivedOn = Input.RequiredDate(request.ReceivedOn, errors, "receivedOn", "The receipt date");
        if (receivedOn > clock.Today)
        {
            errors.Add("receivedOn", "Goods can't be received in the future.");
        }

        var deliveryNote = Input.OptionalText(request.DeliveryNoteNo, ProcurementColumns.ReferenceMaxLength, errors, "deliveryNoteNo", "The delivery note number");
        var notes = Input.OptionalText(request.Notes, ProcurementColumns.NoteMaxLength, errors, "notes", "Notes");
        var lines = request.Lines ?? [];
        if (lines.Count == 0)
        {
            errors.Add("lines", "Receive at least one line.");
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].OrderLineId is not > 0)
            {
                errors.Add($"lines[{index}].orderLineId", "The order line is required.");
            }

            NumberInput.RequiredQuantity(lines[index].Quantity, errors, $"lines[{index}].quantity", "The quantity");
        }

        if (lines.Select(line => line.OrderLineId).Distinct().Count() != lines.Count)
        {
            errors.Add("lines", "Each order line appears once per receipt.");
        }

        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        var order = await db.PurchaseOrders.Include(candidate => candidate.Lines).SingleOrDefaultAsync(candidate => candidate.Id == orderId, cancellationToken);
        var project = await DocumentScope.ProjectAsync(access, httpContext.User, order?.ProjectId, cancellationToken);
        if (order is null || project is null)
        {
            return ProcurementProblems.NotFound("purchase order");
        }

        // The receiving person is on the project's team (engineer or manager), never Purchasing or the office.
        if (project.CallerResponsibility is null && !project.CallerIsExecutive)
        {
            return ProcurementProblems.NotAMember();
        }

        if (project.WritesBlocked)
        {
            return ProcurementProblems.CompletedProject();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(project.ProjectId), cancellationToken))
        {
            return ProcurementProblems.TryAgainLater();
        }

        if (!order.IsOpen)
        {
            return ProcurementProblems.WrongStatus($"Goods are received on an open order. This one is {order.Status}.");
        }

        var received = await ChainQuantities.ReceivedAsync(db, order.Lines.Select(line => line.Id).ToList(), cancellationToken);
        var receipt = new GoodsReceipt
        {
            OrderId = order.Id,
            ProjectId = order.ProjectId,
            ReceivedOn = receivedOn!.Value,
            DeliveryNoteNo = deliveryNote,
            Notes = notes,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };

        foreach (var line in lines)
        {
            var orderLine = order.Lines.SingleOrDefault(candidate => candidate.Id == line.OrderLineId);
            if (orderLine is null)
            {
                return ProcurementProblems.Validation("lines", $"Line {line.OrderLineId} isn't on this order.");
            }

            if (line.Quantity > ProcurementRules.StillDue(orderLine.Quantity, received.GetValueOrDefault(orderLine.Id)))
            {
                return ProcurementProblems.MoreThanOrdered(orderLine.Description);
            }

            receipt.Lines.Add(new GoodsReceiptLine { OrderLineId = orderLine.Id, Quantity = line.Quantity!.Value });
            received[orderLine.Id] = received.GetValueOrDefault(orderLine.Id) + line.Quantity.Value;
        }

        db.GoodsReceipts.Add(receipt);
        order.Received(true, order.Lines.All(line => received.GetValueOrDefault(line.Id) >= line.Quantity));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        log.GoodsReceived(caller.UserId, order.Id, receipt.Id);
        return TypedResults.Created($"/api/procurement/goods-receipts/{receipt.Id}", (await views.ReceiptAsync(receipt.Id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<ReceiptView>, ProblemHttpResult>> GetAsync(
        int receiptId, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, ProcurementViews views, CancellationToken cancellationToken)
    {
        var projectId = await db.GoodsReceipts.AsNoTracking().Where(receipt => receipt.Id == receiptId).Select(receipt => (int?)receipt.ProjectId).SingleOrDefaultAsync(cancellationToken);
        if (await DocumentScope.ProjectAsync(access, httpContext.User, projectId, cancellationToken) is null)
        {
            return ProcurementProblems.NotFound("goods receipt");
        }

        return TypedResults.Ok((await views.ReceiptAsync(receiptId, cancellationToken))!);
    }
}
