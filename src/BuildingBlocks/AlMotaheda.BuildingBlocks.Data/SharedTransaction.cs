using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace AlMotaheda.BuildingBlocks.Data;

/// <summary>
/// One database transaction used by two modules, for the few operations that must change both or neither
/// (a payment request and the source module's payment). All modules share one database, so the module that starts
/// the transaction passes its connection and transaction, and the other module joins it with a short-lived
/// context on the same connection. Both modules' audit entries are written in that same transaction.
/// </summary>
/// <remarks>The joining side must not commit or roll back: the side that started the transaction does.</remarks>
public sealed class SharedTransaction
{
    private SharedTransaction(DbConnection connection, DbTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public DbConnection Connection { get; }

    public DbTransaction Transaction { get; }

    /// <summary>The transaction <paramref name="db"/> is in. Throws when it has none: there is nothing to share.</summary>
    public static SharedTransaction Of(DbContext db) =>
        db.Database.CurrentTransaction is { } current
            ? new SharedTransaction(db.Database.GetDbConnection(), current.GetDbTransaction())
            : throw new InvalidOperationException("Start a transaction before sharing it with another module.");

    /// <summary>
    /// A new context of the joining module on the shared connection and transaction. Dispose it when done; the
    /// connection stays open for the side that owns it.
    /// </summary>
    /// <param name="registered">The module's registered options (interceptors and settings are kept).</param>
    /// <param name="configureSqlServer">The module's SQL Server settings, such as its migration history table.</param>
    /// <param name="create">Creates the module's context from options.</param>
    public async Task<TContext> JoinAsync<TContext>(
        DbContextOptions<TContext> registered,
        Action<SqlServerDbContextOptionsBuilder> configureSqlServer,
        Func<DbContextOptions<TContext>, TContext> create,
        CancellationToken cancellationToken)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>(registered)
            .UseSqlServer(Connection, configureSqlServer)
            .Options;

        var db = create(options);
        await db.Database.UseTransactionAsync(Transaction, cancellationToken);
        return db;
    }
}
