using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AlMotaheda.BuildingBlocks.Data;

/// <summary>
/// The column types every module uses (docs/project-workspace/00-overview.md, section 3.7). Each module's
/// <c>DbContext.ConfigureConventions</c> calls <see cref="Apply"/>, so all schemas store the same kind of value the
/// same way.
/// </summary>
public static class StorageConventions
{
    public static void Apply(ModelConfigurationBuilder configurationBuilder)
    {
        // Moments are stored as UTC DATETIME2(3) (millisecond precision) and read back marked as UTC, so the API
        // always returns times with a trailing "Z". Business dates are DateOnly and stored as DATE.
        configurationBuilder.Properties<DateTime>()
            .HaveColumnType("datetime2(3)")
            .HaveConversion<UtcDateTimeConverter>();

        // Codes and enum names are ASCII. Free text (names, addresses, notes) is configured per column as Unicode.
        configurationBuilder.Properties<string>().AreUnicode(false);

        // Money is DECIMAL(18,2) unless a column says otherwise (quantities are configured as (18,3)).
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
