using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Reflection;
using AlMotaheda.BuildingBlocks.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlMotaheda.Api.Tests;

/// <summary>
/// Lets the real API run its EF Core queries without SQL Server. Every query is translated to SQL exactly as in
/// production; then, instead of being sent, it is recorded and answered with no rows ("does it exist?" answers no,
/// counts answer 0). A query EF can't translate fails here, and tests can check the SQL each endpoint sends.
/// </summary>
/// <remarks>
/// <para>It covers every module's <see cref="DbContext"/> registered in the host, found from the service
/// collection, so a new module is covered without editing this class.</para>
/// <para>Only for reads. Nothing here can commit a transaction, so tests stop before any save (for example at a
/// refused Executive check, or at a project that "doesn't exist").</para>
/// <para>The audit trail is replaced by an interceptor that does nothing: the real one opens a transaction before
/// every save and writes Platform tables this stand-in doesn't have. It is tested against SQL Server.</para>
/// </remarks>
internal sealed class StandInDatabase
{
    private static readonly MethodInfo ConfigureDbContextMethod = typeof(StandInDatabase)
        .GetMethod(nameof(ConfigureDbContext), BindingFlags.NonPublic | BindingFlags.Static)!;

    public ConcurrentQueue<string> Commands { get; } = new();

    public void AddTo(WebApplicationBuilder builder)
    {
        builder.Services.RemoveAllKeyed<ISaveChangesInterceptor>(ModuleDatabase.AuditTrailServiceKey);
        builder.Services.AddKeyedSingleton<ISaveChangesInterceptor, NoAuditTrail>(ModuleDatabase.AuditTrailServiceKey);

        var connections = new NeverOpen();
        var commands = new NoRows(Commands);

        var contextTypes = builder.Services
            .Select(descriptor => descriptor.ServiceType)
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DbContextOptions<>))
            .Select(type => type.GetGenericArguments()[0])
            .Distinct()
            .ToList();

        foreach (var contextType in contextTypes)
        {
            ConfigureDbContextMethod.MakeGenericMethod(contextType).Invoke(null, [builder.Services, connections, commands]);
        }
    }

    private static void ConfigureDbContext<TContext>(IServiceCollection services, NeverOpen connections, NoRows commands)
        where TContext : DbContext =>
        services.ConfigureDbContext<TContext>(options => options.AddInterceptors(connections, commands));

    private sealed class NoAuditTrail : SaveChangesInterceptor;

    private sealed class NeverOpen : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public override InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class NoRows(ConcurrentQueue<string> log) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
            InterceptionResult<DbDataReader>.SuppressWithResult(Answer(command));

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(Answer(command)));

        private DbDataReader Answer(DbCommand command)
        {
            log.Enqueue(command.CommandText);

            var sql = command.CommandText.TrimStart();
            var table = new DataTable();

            if (sql.StartsWith("SELECT CASE", StringComparison.Ordinal))
            {
                table.Columns.Add("Value", typeof(bool));
                table.Rows.Add(false);
            }
            else if (sql.StartsWith("SELECT COUNT(*)", StringComparison.Ordinal))
            {
                table.Columns.Add("Value", typeof(int));
                table.Rows.Add(0);
            }

            return table.CreateDataReader();
        }
    }
}
