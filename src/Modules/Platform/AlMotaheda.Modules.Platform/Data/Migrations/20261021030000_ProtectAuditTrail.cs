using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Platform.Data.Migrations;

/// <summary>
/// Section 17: the audit trail is append-only at the database too. A trigger refuses every UPDATE and DELETE on
/// <c>platform.AuditEntry</c>, so no code path, bug or ordinary login can rewrite or erase history; only a DBA who
/// drops the trigger can, and that is itself visible. Additive: no model change.
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20261021030000_ProtectAuditTrail")]
internal sealed class ProtectAuditTrail : Migration
{
    public const string TriggerName = "TR_AuditEntry_AppendOnly";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            CREATE TRIGGER [platform].[{TriggerName}] ON [platform].[AuditEntry]
            INSTEAD OF UPDATE, DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                THROW 50017, 'The audit trail is append-only: audit entries are never updated or deleted.', 1;
            END
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"DROP TRIGGER IF EXISTS [platform].[{TriggerName}];");
    }
}
