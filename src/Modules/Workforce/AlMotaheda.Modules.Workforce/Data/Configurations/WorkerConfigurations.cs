using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Workforce.Data.Configurations;

internal sealed class TradeConfiguration : IEntityTypeConfiguration<Trade>
{
    /// <summary>One trade per code. A lost race on it becomes 409 <c>trade_code_taken</c>.</summary>
    public const string CodeIndex = "UX_Trade_Code";

    public const int CodeMaxLength = 30;
    public const int NameMaxLength = 200;

    public void Configure(EntityTypeBuilder<Trade> builder)
    {
        builder.ToTable(nameof(Trade), table =>
        {
            table.HasCheckConstraint("CK_Trade_DefaultDailyRate", "[DefaultDailyRate] > 0");
            table.HasCheckConstraint("CK_Trade_DefaultWorkType", $"[DefaultWorkType] IN ({SqlList.Of(WorkTypes.All)})");
        });

        builder.Property(trade => trade.Code).HasMaxLength(CodeMaxLength);
        builder.Property(trade => trade.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(trade => trade.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(trade => trade.DefaultWorkType).HasMaxLength(30);
        builder.Property(trade => trade.RowVersion).IsRowVersion();

        builder.HasIndex(trade => trade.Code).IsUnique().HasDatabaseName(CodeIndex);

        builder.HasData(TradeSeed.All);
    }
}

internal sealed class WorkerConfiguration : IEntityTypeConfiguration<Worker>
{
    /// <summary>One worker per national id. A lost race on it becomes 409 <c>national_id_taken</c>.</summary>
    public const string NationalIdIndex = "UX_Worker_NationalId";

    public const int NameMaxLength = 200;
    public const int NationalIdLength = 14;
    public const int PhoneMaxLength = 30;
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<Worker> builder)
    {
        builder.ToTable(nameof(Worker), table =>
        {
            table.HasCheckConstraint("CK_Worker_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<WorkerStatus>())})");

            // Every nullable column is tested with IS NULL first: a CHECK passes when its result is unknown.
            table.HasCheckConstraint("CK_Worker_DailyRate", "[DailyRate] IS NULL OR [DailyRate] > 0");
            table.HasCheckConstraint(
                "CK_Worker_NationalId",
                "[NationalId] IS NULL OR (LEN([NationalId]) = 14 AND [NationalId] NOT LIKE '%[^0-9]%')");
        });

        builder.Property(worker => worker.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(worker => worker.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(worker => worker.NationalId).HasMaxLength(NationalIdLength);
        builder.Property(worker => worker.Phone).HasMaxLength(PhoneMaxLength);
        builder.Property(worker => worker.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(worker => worker.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(worker => worker.RowVersion).IsRowVersion();

        builder.HasIndex(worker => worker.NationalId)
            .IsUnique()
            .HasFilter("[NationalId] IS NOT NULL")
            .HasDatabaseName(NationalIdIndex);

        // The workers list sorts by English name; the trade filter and the trade lookups use the trade.
        builder.HasIndex(worker => worker.NameEn, "IX_Worker_NameEn");
        builder.HasIndex(worker => worker.TradeId, "IX_Worker_TradeId");

        builder.HasOne(worker => worker.Trade)
            .WithMany()
            .HasForeignKey(worker => worker.TradeId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class WorkerAssignmentConfiguration : IEntityTypeConfiguration<WorkerAssignment>
{
    /// <summary>One open assignment per worker and project. A lost race on it becomes 409 <c>already_assigned</c>.</summary>
    public const string OpenIndex = "UX_WorkerAssignment_Open";

    /// <summary>The foreign key to <c>projects.Project</c>. Added by hand in the migration: Projects owns that table.</summary>
    public const string ProjectForeignKey = "FK_WorkerAssignment_Project_ProjectId";

    public void Configure(EntityTypeBuilder<WorkerAssignment> builder)
    {
        builder.ToTable(nameof(WorkerAssignment), table =>
        {
            table.HasCheckConstraint("CK_WorkerAssignment_DailyRate", "[DailyRate] IS NULL OR [DailyRate] > 0");
            table.HasCheckConstraint("CK_WorkerAssignment_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");

            // An ending is recorded whole: the last day, who and when, or nothing at all.
            table.HasCheckConstraint(
                "CK_WorkerAssignment_Ending",
                "([EndDate] IS NULL AND [EndedAt] IS NULL AND [EndedBy] IS NULL) "
                + "OR ([EndDate] IS NOT NULL AND [EndedAt] IS NOT NULL AND [EndedBy] IS NOT NULL)");
        });

        builder.Property(assignment => assignment.RowVersion).IsRowVersion();

        builder.HasIndex(assignment => new { assignment.WorkerId, assignment.ProjectId })
            .IsUnique()
            .HasFilter("[EndDate] IS NULL")
            .HasDatabaseName(OpenIndex);

        // The workers of a project, and the assignment that covers a worker's day on a project (ended ones too).
        builder.HasIndex(assignment => assignment.ProjectId, "IX_WorkerAssignment_ProjectId");
        builder.HasIndex(assignment => new { assignment.WorkerId, assignment.ProjectId, assignment.StartDate }, "IX_WorkerAssignment_Worker_Project");

        builder.HasOne(assignment => assignment.Worker)
            .WithMany()
            .HasForeignKey(assignment => assignment.WorkerId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
