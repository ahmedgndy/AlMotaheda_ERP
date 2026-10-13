using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AlMotaheda.Modules.Workforce.Data;

/// <summary>
/// The Workforce module's tables, all in the <c>workforce</c> schema. No other module reads them: other modules ask
/// through contracts.
/// </summary>
internal sealed class WorkforceDbContext(DbContextOptions<WorkforceDbContext> options) : DbContext(options)
{
    public const string Schema = "workforce";

    /// <summary>The module keeps its own migration history, so each module's migrations stay independent.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkforceDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Moments are stored as UTC DATETIME2(3) and read back marked as UTC, like the other modules.
        // Business dates are DateOnly and stored as DATE.
        configurationBuilder.Properties<DateTime>()
            .HaveColumnType("datetime2(3)")
            .HaveConversion<UtcDateTimeConverter>();

        // Codes and enum names are ASCII. Free text (names, notes) is configured per column as Unicode.
        configurationBuilder.Properties<string>().AreUnicode(false);

        // Money is DECIMAL(18,2) unless a column says otherwise (quantities are configured as (18,3)).
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>Writes a list of names as an SQL <c>IN (...)</c> list for check constraints.</summary>
internal static class SqlList
{
    public static string Of(IEnumerable<string> values) => string.Join(", ", values.Select(value => $"'{value}'"));
}
