namespace AlMotaheda.BuildingBlocks.Validation;

/// <summary>The <c>page</c> and <c>pageSize</c> parameters every list takes.</summary>
public static class Paging
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxPage = 100_000;

    public static void Validate(int page, int pageSize, ValidationErrors errors)
    {
        if (page is < 1 or > MaxPage)
        {
            errors.Add("page", $"page must be between 1 and {MaxPage}.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors.Add("pageSize", $"pageSize must be between 1 and {MaxPageSize}.");
        }
    }
}
