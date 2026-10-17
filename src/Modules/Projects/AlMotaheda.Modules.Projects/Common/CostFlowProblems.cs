using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>
/// Error responses of the cost-flow features (docs/cost-flow): the master catalog, project selection, price variances,
/// item components and the supplier profile. Shared codes live in <see cref="WorkspaceProblems"/> and
/// <see cref="PlanningProblems"/>.
/// </summary>
internal static class CostFlowProblems
{
    public const string MasterItemExists = "master_item_exists";
    public const string MasterItemInUse = "master_item_in_use";
    public const string MasterItemInactive = "master_item_inactive";
    public const string PriceVarianceOpen = "price_variance_open";
    public const string PriceVarianceDecided = "price_variance_decided";

    public static ProblemHttpResult MasterItemNotFound() => WorkspaceProblems.NotFound("master catalog item");

    public static ProblemHttpResult PriceVarianceNotFound() => WorkspaceProblems.NotFound("price variance");

    public static ProblemHttpResult SupplierNotFound() => WorkspaceProblems.NotFound("supplier");

    public static ProblemHttpResult NameTaken() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, MasterItemExists, "Another master catalog item already has this Arabic or English name.");

    public static ProblemHttpResult InUse(string what) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, MasterItemInUse, $"Projects already use this master catalog item, so its {what} can't change.");

    public static ProblemHttpResult Inactive() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, MasterItemInactive, "This master catalog item is inactive. Reactivate it first.");

    public static ProblemHttpResult VarianceOpen() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            PriceVarianceOpen,
            "This supply's price differs from the reference price and is waiting for a price review (or was not accepted). It can be approved once the price is accepted.");

    public static ProblemHttpResult VarianceDecided(string status) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, PriceVarianceDecided, $"This price variance is already {status}.");
}
