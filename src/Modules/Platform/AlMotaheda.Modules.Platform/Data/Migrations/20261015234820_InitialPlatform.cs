using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Platform.Data.Migrations
{
    /// <inheritdoc />
    internal partial class InitialPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "AuditEntry",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Module = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    EntityType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    EntityKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    Changes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TraceId = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntry", x => x.Id);
                    table.CheckConstraint("CK_AuditEntry_Action", "[Action] IN ('Added', 'Modified', 'Deleted')");
                });

            migrationBuilder.CreateTable(
                name: "Notification",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecipientUserId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    TitleAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    TitleEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    EntityType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    EntityKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    SourceMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notification", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessage", x => x.Id);
                    table.CheckConstraint("CK_OutboxMessage_Attempts", "[Attempts] >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntry_Entity",
                schema: "platform",
                table: "AuditEntry",
                columns: new[] { "EntityType", "EntityKey", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntry_OccurredAt",
                schema: "platform",
                table: "AuditEntry",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntry_Project_OccurredAt",
                schema: "platform",
                table: "AuditEntry",
                columns: new[] { "ProjectId", "OccurredAt" },
                filter: "[ProjectId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntry_User_OccurredAt",
                schema: "platform",
                table: "AuditEntry",
                columns: new[] { "UserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notification_Recipient",
                schema: "platform",
                table: "Notification",
                columns: new[] { "RecipientUserId", "Id" })
                .Annotation("SqlServer:Include", new[] { "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Notification_Source",
                schema: "platform",
                table: "Notification",
                columns: new[] { "SourceMessageId", "RecipientUserId", "EntityType", "EntityKey", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_Pending",
                schema: "platform",
                table: "OutboxMessage",
                column: "Id",
                filter: "[ProcessedAt] IS NULL")
                .Annotation("SqlServer:Include", new[] { "Attempts" });

            migrationBuilder.CreateIndex(
                name: "UX_OutboxMessage_MessageId",
                schema: "platform",
                table: "OutboxMessage",
                column: "MessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntry",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Notification",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "platform");
        }
    }
}
