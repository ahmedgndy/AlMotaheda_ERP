using System.Text.RegularExpressions;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Trades;

/// <summary>The fields of the trade form, shared by create and edit.</summary>
internal interface ITradeFieldsRequest
{
    string? Code { get; }
    string? NameAr { get; }
    string? NameEn { get; }
    decimal? DefaultDailyRate { get; }
    string? DefaultWorkType { get; }
}

/// <summary>A trade form after validation.</summary>
internal sealed record TradeFields(string Code, string NameAr, string NameEn, decimal DefaultDailyRate, string DefaultWorkType)
{
    public void ApplyTo(Trade trade)
    {
        trade.Code = Code;
        trade.NameAr = NameAr;
        trade.NameEn = NameEn;
        trade.DefaultDailyRate = DefaultDailyRate;
        trade.DefaultWorkType = DefaultWorkType;
    }
}

/// <summary>The input rules of the trade form. Every failing field is reported at once.</summary>
internal static partial class TradeForm
{
    public static TradeFields? Validate(ITradeFieldsRequest request, ValidationErrors errors)
    {
        var code = Input.Clean(request.Code)?.ToLowerInvariant();
        if (code is null)
        {
            errors.Add("code", "The code is required.");
        }
        else if (!CodePattern().IsMatch(code))
        {
            errors.Add("code", $"The code must be 2 to {TradeConfiguration.CodeMaxLength} letters, digits, '-' or '_' (for example plumber).");
        }

        var nameAr = Input.RequiredText(request.NameAr, TradeConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var nameEn = Input.RequiredText(request.NameEn, TradeConfiguration.NameMaxLength, errors, "nameEn", "The English name");
        var rate = NumberInput.RequiredMoney(request.DefaultDailyRate, errors, "defaultDailyRate", "The default daily rate");

        string? workType = null;
        if (Input.Clean(request.DefaultWorkType) is null)
        {
            errors.Add("defaultWorkType", "The default work type is required.");
        }
        else if (WorkTypes.TryParse(request.DefaultWorkType, out var parsed))
        {
            workType = parsed;
        }
        else
        {
            errors.Add("defaultWorkType", $"defaultWorkType must be one of: {string.Join(", ", WorkTypes.All)}.");
        }

        return errors.HasErrors ? null : new TradeFields(code!, nameAr!, nameEn!, rate!.Value, workType!);
    }

    [GeneratedRegex("^[a-z0-9_-]{2,30}$")]
    private static partial Regex CodePattern();
}

/// <summary>A trade as the API returns it. The rate is <c>null</c> without workers See money.</summary>
internal sealed record TradeView(
    int Id,
    string Code,
    string NameAr,
    string NameEn,
    decimal? DefaultDailyRate,
    string DefaultWorkType,
    bool IsActive,
    string RowVersion)
{
    public static TradeView From(Trade trade, bool seesMoney) => new(
        trade.Id,
        trade.Code,
        trade.NameAr,
        trade.NameEn,
        seesMoney ? trade.DefaultDailyRate : null,
        trade.DefaultWorkType,
        trade.IsActive,
        RowVersions.Format(trade.RowVersion));
}

/// <summary>The trade queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class TradeQueries
{
    /// <summary>Every trade by English name; only the active ones when asked.</summary>
    public static IQueryable<Trade> List(WorkforceDbContext db, bool activeOnly)
    {
        var query = db.Trades.AsNoTracking();
        if (activeOnly)
        {
            query = query.Where(trade => trade.IsActive);
        }

        return query.OrderBy(trade => trade.NameEn).ThenBy(trade => trade.Id);
    }
}
