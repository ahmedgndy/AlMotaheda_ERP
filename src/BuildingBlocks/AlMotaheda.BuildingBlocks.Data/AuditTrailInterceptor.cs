using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AlMotaheda.BuildingBlocks.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace AlMotaheda.BuildingBlocks.Data;

/// <summary>
/// Writes the audit trail and the outbox of every save of every module, in the same transaction as the save.
/// </summary>
/// <remarks>
/// <para>For each added, changed or deleted row (except <see cref="NotAuditedAttribute"/> ones) it writes one
/// <c>platform.AuditEntry</c>: who, when, which record, and the old and new value of every field that changed.
/// It also writes one <c>platform.OutboxMessage</c> listing the records that changed (no amounts), which the
/// Platform module turns into notifications after the commit.</para>
/// <para>Both are written inside the transaction of the save: the handler's own transaction when it has one,
/// otherwise one this interceptor opens before the save and commits after it. So a change is never saved
/// without its audit, and a rolled-back change leaves no audit.</para>
/// <para>Not covered: bulk <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> statements, which bypass the change tracker.
/// Only the Access module uses them (sign-in bookkeeping, and removing permission overrides, which its
/// security log records).</para>
/// </remarks>
public sealed class AuditTrailInterceptor(IHttpContextAccessor httpContextAccessor, TimeProvider time) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ConditionalWeakTable<Type, StrongBox<bool>> AuditedTypes = new();

    // One pending save per DbContext. A context runs one save at a time, so this never collides.
    private readonly ConditionalWeakTable<DbContext, PendingSave> _pending = new();

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } db && Capture(db) is { } captured)
        {
            var owned = db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;
            _pending.AddOrUpdate(db, new PendingSave(captured, owned));
        }

        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } db && Capture(db) is { } captured)
        {
            var owned = db.Database.CurrentTransaction is null ? db.Database.BeginTransaction() : null;
            _pending.AddOrUpdate(db, new PendingSave(captured, owned));
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } db && _pending.TryGetValue(db, out var pending))
        {
            _pending.Remove(db);
            await using var owned = pending.OwnedTransaction;

            var (sql, parameters) = BuildInsert(pending.Captured);
            await db.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);

            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken);
            }
        }

        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } db && _pending.TryGetValue(db, out var pending))
        {
            _pending.Remove(db);
            using var owned = pending.OwnedTransaction;

            var (sql, parameters) = BuildInsert(pending.Captured);
            db.Database.ExecuteSqlRaw(sql, parameters);
            owned?.Commit();
        }

        return result;
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default) =>
        await DiscardAsync(eventData.Context);

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => DiscardAsync(eventData.Context).AsTask().GetAwaiter().GetResult();

    public override async Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default) =>
        await DiscardAsync(eventData.Context);

    /// <summary>The save failed: drop what was captured and roll back the transaction this interceptor opened.</summary>
    private async ValueTask DiscardAsync(DbContext? db)
    {
        if (db is not null && _pending.TryGetValue(db, out var pending))
        {
            _pending.Remove(db);
            if (pending.OwnedTransaction is { } owned)
            {
                // Disposing a transaction that wasn't committed rolls it back.
                await owned.DisposeAsync();
            }
        }
    }

    /// <summary>The audited rows of this save, read before EF saves them (old values are still known), or null.</summary>
    private static List<CapturedChange>? Capture(DbContext db)
    {
        db.ChangeTracker.DetectChanges();

        var captured = db.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                            && IsAudited(entry.Metadata.ClrType))
            .Select(entry => CapturedChange.From(entry, db.Model.GetDefaultSchema() ?? ""))
            .Where(change => change is not null)
            .Select(change => change!)
            .ToList();

        return captured.Count == 0 ? null : captured;
    }

    private (string Sql, object[] Parameters) BuildInsert(IReadOnlyList<CapturedChange> captured)
    {
        var occurredAt = time.GetUtcNow().UtcDateTime;
        var userId = httpContextAccessor.HttpContext?.User.GetUserId();
        var traceId = Activity.Current?.TraceId.ToHexString();

        var changes = captured.Select(change => change.Complete()).ToList();

        var auditRows = JsonSerializer.Serialize(
            changes.Select(change => new
            {
                change.Module,
                change.Entity,
                change.Key,
                change.Action,
                change.ProjectId,
                Changes = change.Values,
            }),
            Json);

        var payload = JsonSerializer.Serialize(new EntitiesChangedPayload(changes.Select(change => change.ToEntityChange()).ToList()), Json);

        var sql = $"""
            INSERT INTO [{PlatformTables.Schema}].[{PlatformTables.AuditEntry}]
                ([OccurredAt], [UserId], [Module], [EntityType], [EntityKey], [Action], [ProjectId], [Changes], [TraceId])
            SELECT @occurredAt, @userId, [Module], [Entity], [Key], [Action], [ProjectId], [Changes], @traceId
            FROM OPENJSON(@auditRows) WITH (
                [Module] varchar(30) '$.module',
                [Entity] varchar(100) '$.entity',
                [Key] varchar(100) '$.key',
                [Action] varchar(10) '$.action',
                [ProjectId] int '$.projectId',
                [Changes] nvarchar(max) '$.changes' AS JSON);

            INSERT INTO [{PlatformTables.Schema}].[{PlatformTables.OutboxMessage}]
                ([MessageId], [Type], [Payload], [OccurredAt], [UserId], [Attempts])
            VALUES (@messageId, @type, @payload, @occurredAt, @userId, 0);
            """;

        object[] parameters =
        [
            new SqlParameter("@occurredAt", System.Data.SqlDbType.DateTime2) { Value = occurredAt, Scale = 3 },
            new SqlParameter("@userId", System.Data.SqlDbType.Int) { Value = (object?)userId ?? DBNull.Value },
            new SqlParameter("@traceId", System.Data.SqlDbType.VarChar, 32) { Value = (object?)traceId ?? DBNull.Value },
            new SqlParameter("@auditRows", System.Data.SqlDbType.NVarChar, -1) { Value = auditRows },
            new SqlParameter("@messageId", System.Data.SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() },
            new SqlParameter("@type", System.Data.SqlDbType.VarChar, 100) { Value = PlatformTables.EntitiesChangedMessage },
            new SqlParameter("@payload", System.Data.SqlDbType.NVarChar, -1) { Value = payload },
        ];

        return (sql, parameters);
    }

    private static bool IsAudited(Type type) =>
        AuditedTypes.GetValue(type, candidate => new StrongBox<bool>(candidate.GetCustomAttribute<NotAuditedAttribute>() is null)).Value;

    private sealed record PendingSave(List<CapturedChange> Captured, IDbContextTransaction? OwnedTransaction);

    /// <summary>One row of the save. Keys of added rows are generated by the database, so they are read after the save.</summary>
    private sealed class CapturedChange
    {
        private EntityEntry _entry = null!;

        public string Module { get; private init; } = "";
        public string Entity { get; private init; } = "";
        public string Action { get; private init; } = "";
        public int? ProjectId { get; private init; }
        public string? StatusBefore { get; private init; }
        public string? StatusAfter { get; private init; }
        public Dictionary<string, int> People { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, object?[]> Values { get; } = new(StringComparer.Ordinal);

        public string Key { get; private set; } = "";

        private static string ReadKey(EntityEntry entry) =>
            entry.Metadata.FindPrimaryKey() is { } key
                ? string.Join(",", key.Properties.Select(property => Convert.ToString(
                    entry.Property(property.Name).CurrentValue, System.Globalization.CultureInfo.InvariantCulture)))
                : "";

        public static CapturedChange? From(EntityEntry entry, string module)
        {
            var action = entry.State switch
            {
                EntityState.Added => "Added",
                EntityState.Deleted => "Deleted",
                _ => "Modified",
            };

            var change = new CapturedChange
            {
                _entry = entry,
                // Added rows get their key from the database during the save (see Complete).
                Key = entry.State == EntityState.Added ? "" : ReadKey(entry),
                Module = module,
                Entity = entry.Metadata.ClrType.Name,
                Action = action,
                ProjectId = ReadInt(entry, "ProjectId", useOriginal: entry.State == EntityState.Deleted),
                StatusBefore = entry.State == EntityState.Added ? null : Text(Original(entry, "Status")),
                StatusAfter = entry.State == EntityState.Deleted ? null : Text(Current(entry, "Status")),
            };

            foreach (var property in entry.Properties)
            {
                var metadata = property.Metadata;
                if (metadata.ClrType == typeof(byte[])
                    || metadata.PropertyInfo?.GetCustomAttribute<NotAuditedAttribute>() is not null)
                {
                    continue;
                }

                var name = metadata.Name;
                if ((name.EndsWith("By", StringComparison.Ordinal) || name.EndsWith("UserId", StringComparison.Ordinal))
                    && ReadInt(entry, name, useOriginal: entry.State == EntityState.Deleted) is { } person)
                {
                    change.People[name] = person;
                }

                switch (entry.State)
                {
                    case EntityState.Added when !metadata.IsPrimaryKey() && property.CurrentValue is not null:
                        change.Values[name] = [null, Normalize(property.CurrentValue)];
                        break;
                    case EntityState.Modified when property.IsModified && !Equals(property.OriginalValue, property.CurrentValue):
                        change.Values[name] = [Normalize(property.OriginalValue), Normalize(property.CurrentValue)];
                        break;
                    case EntityState.Deleted when property.OriginalValue is not null:
                        change.Values[name] = [Normalize(property.OriginalValue), null];
                        break;
                }
            }

            // A row marked Modified with no real change (same values written back) is not worth an audit entry.
            return entry.State == EntityState.Modified && change.Values.Count == 0 ? null : change;
        }

        /// <summary>Reads the key of an added row, now that the database has generated it.</summary>
        public CapturedChange Complete()
        {
            if (Action == "Added")
            {
                Key = ReadKey(_entry);
            }

            return this;
        }

        public EntityChange ToEntityChange() =>
            new(Module, Entity, Key, Action, ProjectId, StatusBefore, StatusAfter, People, Values.Keys.ToList());

        private static object? Original(EntityEntry entry, string name) =>
            entry.Metadata.FindProperty(name) is null ? null : entry.Property(name).OriginalValue;

        private static object? Current(EntityEntry entry, string name) =>
            entry.Metadata.FindProperty(name) is null ? null : entry.Property(name).CurrentValue;

        private static int? ReadInt(EntityEntry entry, string name, bool useOriginal) =>
            (useOriginal ? Original(entry, name) : Current(entry, name)) as int?;

        private static string? Text(object? value) => value?.ToString();

        private static object? Normalize(object? value) => value switch
        {
            null => null,
            Enum enumValue => enumValue.ToString(),
            DateOnly date => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            DateTime moment => moment.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            _ => value,
        };
    }
}

/// <summary>The payload of the <see cref="PlatformTables.EntitiesChangedMessage"/> outbox message.</summary>
public sealed record EntitiesChangedPayload(IReadOnlyList<EntityChange> Changes);

/// <summary>
/// One record that changed in a save. Carries no amounts or names: only what is needed to tell people about it and
/// to refresh what depends on it.
/// </summary>
/// <param name="Module">The schema of the module that owns the record, for example <c>projects</c>.</param>
/// <param name="Entity">The entity type name, for example <c>ProjectExpense</c>.</param>
/// <param name="Key">The primary key; several values are separated by commas.</param>
/// <param name="Action"><c>Added</c>, <c>Modified</c> or <c>Deleted</c>.</param>
/// <param name="People">The record's user fields (<c>CreatedBy</c>, <c>RecipientUserId</c>, …) after the change.</param>
/// <param name="Fields">The names of the fields that changed.</param>
public sealed record EntityChange(
    string Module,
    string Entity,
    string Key,
    string Action,
    int? ProjectId,
    string? StatusBefore,
    string? StatusAfter,
    IReadOnlyDictionary<string, int> People,
    IReadOnlyList<string> Fields);
