using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using AlMotaheda.Modules.Access.Data;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Projects.Tests.Api;

/// <summary>
/// Lets the real API run its EF Core queries without SQL Server. Every query is translated to SQL exactly as in
/// production; then, instead of being sent, it is recorded and answered with no rows ("does it exist?" answers no,
/// counts answer 0). A query EF can't translate fails here, and tests can check the SQL each endpoint sends.
/// </summary>
/// <remarks>
/// Only for reads. Nothing here can commit a transaction, so tests stop before any save (for example at a
/// refused Executive check, or at a project that "doesn't exist").
/// </remarks>
internal sealed class StandInDatabase
{
    public ConcurrentQueue<string> Commands { get; } = new();

    public void AddTo(WebApplicationBuilder builder)
    {
        var connections = new NeverOpen();
        var commands = new NoRows(Commands);

        builder.Services.ConfigureDbContext<ProjectsDbContext>(options => options.AddInterceptors(connections, commands));
        builder.Services.ConfigureDbContext<AccessDbContext>(options => options.AddInterceptors(connections, commands));
    }

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
