namespace AlMotaheda.Modules.Projects.Common;

/// <summary>Start and end dates of memberships. Access follows the row, so dates can't be in the future (D12).</summary>
internal static class MemberDates
{
    public static DateOnly Start(string? value, DateOnly today, ValidationErrors errors)
    {
        var date = Input.OptionalDate(value, errors, "startDate", "Start date") ?? today;
        if (date > today)
        {
            errors.Add("startDate", "The start date can't be in the future: access starts when the person is added.");
        }

        return date;
    }

    public static DateOnly End(string? value, DateOnly today, ValidationErrors errors)
    {
        var date = Input.OptionalDate(value, errors, "endDate", "End date") ?? today;
        if (date > today)
        {
            errors.Add("endDate", "The end date can't be in the future: access stops when the membership is ended.");
        }

        return date;
    }
}
