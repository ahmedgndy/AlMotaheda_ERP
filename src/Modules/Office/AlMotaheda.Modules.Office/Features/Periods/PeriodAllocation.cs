using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Data.Configurations;
using AlMotaheda.Modules.Office.Features.Funding;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Office.Features.Periods;

/// <summary>
/// A month's allocation ("العهدة الشهرية"). It never adds money by itself: authorizing it records the funding request
/// (kind <see cref="OfficeFundingKind.MonthlyAllocation"/>, already agreed by the authorizing reviewer) and asks
/// Finance to pay it, so the company's payments stay in Finance and the Executive approves it there. The month's
/// <c>Allocated</c> figure grows only when the custodian confirms receiving the cash, once.
/// </summary>
internal static class PeriodAllocation
{
    /// <summary>
    /// Records the allocation of <paramref name="period"/> as agreed by <paramref name="authorizedBy"/> and asks Finance to
    /// pay it, in the caller's transaction (under the custody's lock). The unique index keeps one live allocation per month.
    /// </summary>
    public static async Task<OfficeFundingRequest> RequestAsync(
        OfficeDbContext db,
        IPaymentRequests payments,
        OfficeCustody custody,
        OfficeCustodyPeriod period,
        decimal amount,
        string method,
        string? note,
        Guid idempotencyKey,
        int authorizedBy,
        DateTime now,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var funding = new OfficeFundingRequest
        {
            CustodyId = custody.Id,
            Kind = OfficeFundingKind.MonthlyAllocation,
            PeriodId = period.Id,
            RequestedAmount = amount,
            Reason = $"Monthly allocation {period.Year}-{period.Month:00}",
            RequestedDate = today,
            IdempotencyKey = idempotencyKey,
            RequestedBy = authorizedBy,
            RequestedAt = now,
            Status = OfficeFundingStatus.Approved,
            ApprovedAmount = amount,
            ReviewedBy = authorizedBy,
            ReviewedAt = now,
            ReviewNote = note,
            PaymentMethod = method,
        };
        db.FundingRequests.Add(funding);
        await db.SaveChangesAsync(cancellationToken);

        var key = OfficeFundingEndpoints.PaymentKey(funding.Id);
        funding.PaymentRequestKey = key;
        var created = await payments.SubmitAsync(
            new NewPaymentRequest(
                PaymentSourceTypes.OfficeCustodyFunding,
                PayeeTypes.OfficeCustody,
                funding.Id,
                null,
                PaymentPurposes.OfficeCustodyAdvance,
                amount,
                method,
                today,
                null,
                $"Office custody allocation {period.Year}-{period.Month:00} — {custody.NameEn}",
                key,
                authorizedBy,
                []),
            SharedTransaction.Of(db),
            cancellationToken);
        funding.PaymentRequestId = created.Id;
        funding.PaymentRequestNumber = created.Number;
        await db.SaveChangesAsync(cancellationToken);
        return funding;
    }

    /// <summary>The month's live allocation (asked, paid out or received; not rejected or cancelled), if any.</summary>
    public static Task<OfficeFundingRequest?> LiveAsync(OfficeDbContext db, int periodId, CancellationToken cancellationToken) =>
        db.FundingRequests.Where(funding => funding.PeriodId == periodId && funding.Status != OfficeFundingStatus.Rejected
                                            && funding.Status != OfficeFundingStatus.Cancelled)
            .OrderByDescending(funding => funding.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The key of the allocation the background check asks for a month: the same whatever the retries.</summary>
    public static Guid AutomaticKey(int periodId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData([.. BitConverter.GetBytes(periodId), .. "OfficeMonthlyAllocation"u8]);
        return new Guid(hash.AsSpan(0, 16));
    }

    public static bool IsAllocationConflict(DbUpdateException exception) =>
        SqlViolations.IsUnique(exception, OfficeFundingRequestConfiguration.AllocationIndex);
}
