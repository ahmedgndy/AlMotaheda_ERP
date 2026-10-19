using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>Error responses of the company price list and the price check of purchases.</summary>
internal static class PriceProblems
{
    public const string PriceItemExists = "price_item_exists";
    public const string PriceAboveRange = "price_above_range";
    public const string PriceNeedsExecutive = "price_needs_executive";
    public const string PriceItemInactive = "price_item_inactive";

    public static ProblemHttpResult PriceItemNotFound() => WorkspaceProblems.NotFound("price item");

    public static ProblemHttpResult PriceRangeNotFound() => WorkspaceProblems.NotFound("price range");

    public static ProblemHttpResult NameTaken() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, PriceItemExists, "The price list already has an item with this Arabic or English name.");

    /// <summary>One request per item and supplier waits for a decision at a time.</summary>
    public static ProblemHttpResult ChangePending() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, PlanningProblems.PriceChangePending, "A price change for this item and supplier is already waiting for a decision.");

    public static ProblemHttpResult DateTaken() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, PlanningProblems.PriceDateTaken, "An approved range for this item and supplier already starts on this date.");

    public static ProblemHttpResult ItemInactive() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, PriceItemInactive, "This price list item is inactive. Choose another item or reactivate it.");

    /// <summary>The policy is Block and the unit price is above the maximum of the reference range.</summary>
    public static ProblemHttpResult AboveRangeBlocked(decimal unitPrice, decimal maxPrice) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, PriceAboveRange,
            $"The unit price {unitPrice:0.00} is above the highest reference price {maxPrice:0.00}. The company policy doesn't allow it.");

    /// <summary>The policy is ExecutiveApproval and the record is above its reference range.</summary>
    public static ProblemHttpResult ApprovedByExecutive() =>
        ApiProblem.Create(StatusCodes.Status403Forbidden, PriceNeedsExecutive, "This purchase is above its reference price range: only an Executive can approve it.");
}
