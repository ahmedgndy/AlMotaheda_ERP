namespace AlMotaheda.BuildingBlocks.Validation;

/// <summary>Input rules for numbers, added to <see cref="Input"/>'s rules for text and dates.</summary>
public static class NumberInput
{
    /// <summary>The largest amount accepted in one field. Far above any real invoice; stops overflow of DECIMAL(18,2) sums.</summary>
    public const decimal MaxMoney = 9_999_999_999.99m;

    public const decimal MaxQuantity = 999_999_999.999m;

    /// <summary>A required amount of money greater than zero, with at most two decimals.</summary>
    public static decimal? RequiredMoney(decimal? value, ValidationErrors errors, string field, string label)
    {
        if (value is null)
        {
            errors.Add(field, $"{label} is required.");
            return null;
        }

        return Money(value.Value, errors, field, label);
    }

    /// <summary>An optional amount of money greater than zero, with at most two decimals.</summary>
    public static decimal? OptionalMoney(decimal? value, ValidationErrors errors, string field, string label) =>
        value is null ? null : Money(value.Value, errors, field, label);

    /// <summary>A required quantity greater than zero, with at most three decimals.</summary>
    public static decimal? RequiredQuantity(decimal? value, ValidationErrors errors, string field, string label)
    {
        if (value is null)
        {
            errors.Add(field, $"{label} is required.");
            return null;
        }

        if (value <= 0)
        {
            errors.Add(field, $"{label} must be greater than zero.");
            return null;
        }

        if (value > MaxQuantity || decimal.Round(value.Value, 3) != value.Value)
        {
            errors.Add(field, $"{label} must have at most three decimals and be at most {MaxQuantity:0}.");
            return null;
        }

        return value;
    }

    private static decimal? Money(decimal value, ValidationErrors errors, string field, string label)
    {
        if (value <= 0)
        {
            errors.Add(field, $"{label} must be greater than zero.");
            return null;
        }

        if (value > MaxMoney || decimal.Round(value, 2) != value)
        {
            errors.Add(field, $"{label} must have at most two decimals and be at most {MaxMoney:0.00}.");
            return null;
        }

        return value;
    }
}
