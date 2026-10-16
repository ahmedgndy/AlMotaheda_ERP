using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlMotaheda.BuildingBlocks.Data;

/// <summary>Registers a module's <see cref="DbContext"/> the same way for every module.</summary>
public static class ModuleDatabase
{
    /// <summary>The connection string name in configuration: <c>ConnectionStrings:Erp</c>. One database, one schema per module.</summary>
    public const string ConnectionStringName = "Erp";

    /// <summary>
    /// The service key of the audit-trail interceptor every audited context uses. Tests that run without SQL Server
    /// replace it with one that does nothing, because the stand-in database has no Platform tables.
    /// </summary>
    public const string AuditTrailServiceKey = "audit-trail";

    /// <summary>
    /// Adds the module's context on SQL Server, with the audit trail and outbox (<see cref="AuditTrailInterceptor"/>)
    /// unless <paramref name="audited"/> is false (only the Platform module, which owns those tables).
    /// </summary>
    /// <param name="configureSqlServer">The module's SQL Server settings, such as its migration history table.</param>
    public static IServiceCollection AddModuleDbContext<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<SqlServerDbContextOptionsBuilder> configureSqlServer,
        bool audited = true)
        where TContext : DbContext
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} is missing. Set it with user secrets in development "
                + $"or the ConnectionStrings__{ConnectionStringName} environment variable on the server.");
        }

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddKeyedSingleton<ISaveChangesInterceptor, AuditTrailInterceptor>(AuditTrailServiceKey);

        services.AddDbContext<TContext>((provider, options) =>
        {
            options
                .UseSqlServer(connectionString, configureSqlServer)
                // Failures are logged once, by the host's exception handler; expected ones (a duplicate key) become 409s.
                // EF's own "save failed" entry would also copy duplicate key values (emails, codes) into the logs.
                .ConfigureWarnings(warnings => warnings.Ignore(
                    CoreEventId.SaveChangesFailed,
                    CoreEventId.QueryIterationFailed,
                    RelationalEventId.CommandError,
                    RelationalEventId.ConnectionError));

            if (audited)
            {
                options.AddInterceptors(provider.GetRequiredKeyedService<ISaveChangesInterceptor>(AuditTrailServiceKey));
            }
        });

        return services;
    }
}
