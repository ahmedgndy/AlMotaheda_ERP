using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AlMotaheda.Modules.Access.Data;

/// <summary>
/// The Access module's tables, all in the <c>access</c> schema. No other module reads them.
/// </summary>
internal sealed class AccessDbContext(DbContextOptions<AccessDbContext> options) : DbContext(options)
{
    public const string Schema = "access";

    /// <summary>The module keeps its own migration history, so each module's migrations stay independent.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSignInState> UserSignInStates => Set<UserSignInState>();
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AccessDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every time is stored as UTC DATETIME2(3) (millisecond precision) and read back marked as UTC,
        // so the API always returns times with a trailing "Z".
        configurationBuilder.Properties<DateTime>()
            .HaveColumnType("datetime2(3)")
            .HaveConversion<UtcDateTimeConverter>();

        // Codes and enum names are ASCII. Free text (names, emails) is configured per column as Unicode.
        configurationBuilder.Properties<string>().AreUnicode(false);
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
