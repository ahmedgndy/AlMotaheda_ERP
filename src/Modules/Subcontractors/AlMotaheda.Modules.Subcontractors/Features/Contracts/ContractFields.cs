using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Data.Configurations;

namespace AlMotaheda.Modules.Subcontractors.Features.Contracts;

/// <summary>The contract form, as sent on create and on edit (the company is chosen on create only).</summary>
internal interface IContractFieldsRequest
{
    string? Model { get; }
    decimal? Value { get; }
    decimal? DailyRate { get; }
    decimal? RetentionPercent { get; }
    string? StartDate { get; }
    string? EndDate { get; }
    string? Status { get; }
    string? ContractNo { get; }
    string? Scope { get; }
}

/// <summary>The contract form after validation.</summary>
internal sealed record ContractFields(
    ContractModel Model,
    decimal Value,
    decimal? DailyRate,
    decimal RetentionPercent,
    DateOnly StartDate,
    DateOnly? EndDate,
    ContractStatus Status,
    string? ContractNo,
    string? Scope)
{
    /// <summary>Checks every field and reports every problem at once.</summary>
    public static ContractFields? Validate(IContractFieldsRequest request, ValidationErrors errors)
    {
        if (!Input.TryParseName<ContractModel>(request.Model, out var model))
        {
            errors.Add("model", $"model must be one of: {string.Join(", ", Enum.GetNames<ContractModel>())}.");
        }

        var value = NumberInput.RequiredMoney(request.Value, errors, "value", "The contract value");

        // A daily rate belongs to a daily-rate contract only, and is required there.
        decimal? dailyRate = null;
        if (!errors.Has("model"))
        {
            if (model == ContractModel.DailyRate)
            {
                dailyRate = NumberInput.RequiredMoney(request.DailyRate, errors, "dailyRate", "The daily rate");
            }
            else if (request.DailyRate is not null)
            {
                errors.Add("dailyRate", "A daily rate is only for a DailyRate contract.");
            }
        }

        var retention = request.RetentionPercent ?? 0;
        if (retention is < 0 or > 100 || decimal.Round(retention, 2) != retention)
        {
            errors.Add("retentionPercent", "retentionPercent must be between 0 and 100, with at most two decimals.");
        }

        var startDate = Input.RequiredDate(request.StartDate, errors, "startDate", "The start date");
        var endDate = Input.OptionalDate(request.EndDate, errors, "endDate", "The end date");
        if (startDate is { } start && endDate is { } end && end < start)
        {
            errors.Add("endDate", "The end date can't be before the start date.");
        }

        ContractStatus status = ContractStatus.Active;
        if (Input.Clean(request.Status) is not null && !Input.TryParseName(request.Status, out status))
        {
            errors.Add("status", $"status must be one of: {string.Join(", ", Enum.GetNames<ContractStatus>())}.");
        }

        var contractNo = Input.OptionalText(request.ContractNo, SubContractConfiguration.ContractNoMaxLength, errors, "contractNo", "The contract number");
        var scope = Input.OptionalText(request.Scope, SubContractConfiguration.ScopeMaxLength, errors, "scope", "The scope");

        return errors.HasErrors
            ? null
            : new ContractFields(model, value!.Value, dailyRate, retention, startDate!.Value, endDate, status, contractNo, scope);
    }

    public void ApplyTo(SubContract contract)
    {
        contract.Model = Model;
        contract.Value = Value;
        contract.DailyRate = DailyRate;
        contract.RetentionPercent = RetentionPercent;
        contract.StartDate = StartDate;
        contract.EndDate = EndDate;
        contract.Status = Status;
        contract.ContractNo = ContractNo;
        contract.Scope = Scope;
    }
}
