namespace AlMotaheda.Modules.Workforce.Data;

/// <summary>
/// A trade a daily worker can work in (mason, plumber, guard...). A company-wide list, seeded with the prototype's
/// 16 trades. A trade gives the default daily rate and the default work type of a labor line.
/// </summary>
/// <remarks>
/// A trade is never deleted: labor lines point to it. An inactive trade keeps its history but can't be chosen for
/// new lines or new workers.
/// </remarks>
internal sealed class Trade
{
    public int Id { get; set; }

    /// <summary>A short stable key, such as <c>plumber</c>. Unique.</summary>
    public string Code { get; set; } = "";

    public string NameAr { get; set; } = "";

    public string NameEn { get; set; } = "";

    /// <summary>The rate of a worker who has no rate of their own, and of anyone who works a day in this trade outside their own trade.</summary>
    public decimal DefaultDailyRate { get; set; }

    /// <summary>One of <c>WorkTypes</c>: the work type a new line in this trade gets unless the recorder picks another.</summary>
    public string DefaultWorkType { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public byte[] RowVersion { get; set; } = [];
}
