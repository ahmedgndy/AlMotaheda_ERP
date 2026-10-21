using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Projects.Data.Configurations;

internal sealed class BudgetConfiguration : IEntityTypeConfiguration<Budget>
{
    /// <summary>Version numbers are unique within a project.</summary>
    public const string VersionIndex = "UX_Budget_Project_Version";

    /// <summary>One draft per project. A lost race is answered with 409 <c>budget_draft_exists</c>.</summary>
    public const string DraftIndex = "UX_Budget_Draft";

    /// <summary>One approved budget per project: approving a version supersedes the previous one first.</summary>
    public const string ApprovedIndex = "UX_Budget_Approved";

    public const int TitleMaxLength = 200;
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<Budget> builder)
    {
        builder.ToTable(nameof(Budget), table =>
        {
            table.HasCheckConstraint("CK_Budget_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<BudgetStatus>())})");
            table.HasCheckConstraint("CK_Budget_Version", "[Version] >= 1");

            // An approval is recorded exactly when the version has left Draft. A superseded version keeps it.
            table.HasCheckConstraint(
                "CK_Budget_Approval",
                "([Status] = 'Draft' AND [ApprovedAt] IS NULL AND [ApprovedBy] IS NULL) "
                + "OR ([Status] <> 'Draft' AND [ApprovedAt] IS NOT NULL AND [ApprovedBy] IS NOT NULL)");
        });

        builder.Property(budget => budget.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(budget => budget.Title).HasMaxLength(TitleMaxLength).IsUnicode();
        builder.Property(budget => budget.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(budget => budget.RowVersion).IsRowVersion();

        builder.HasIndex(budget => new { budget.ProjectId, budget.Version }, VersionIndex).IsUnique();

        builder.HasIndex(budget => budget.ProjectId, DraftIndex)
            .IsUnique()
            .HasFilter($"[Status] = '{nameof(BudgetStatus.Draft)}'");

        builder.HasIndex(budget => budget.ProjectId, ApprovedIndex)
            .IsUnique()
            .HasFilter($"[Status] = '{nameof(BudgetStatus.Approved)}'");

        builder.HasOne(budget => budget.Project)
            .WithMany()
            .HasForeignKey(budget => budget.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(budget => budget.Lines)
            .WithOne(line => line.Budget)
            .HasForeignKey(line => line.BudgetId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class BudgetLineConfiguration : IEntityTypeConfiguration<BudgetLine>
{
    public const string LineNoIndex = "UX_BudgetLine_Budget_LineNo";

    public const int DescriptionMaxLength = 300;

    public void Configure(EntityTypeBuilder<BudgetLine> builder)
    {
        builder.ToTable(nameof(BudgetLine), table =>
        {
            table.HasCheckConstraint("CK_BudgetLine_CostGroup", $"[CostGroup] IN ({SqlList.Of(CostGroups.All)})");
            table.HasCheckConstraint("CK_BudgetLine_Unit", $"[Unit] IS NULL OR [Unit] IN ({SqlList.Of(MeasurementUnits.All)})");
            table.HasCheckConstraint("CK_BudgetLine_LineNo", "[LineNo] >= 1");
            table.HasCheckConstraint("CK_BudgetLine_Amount", "[Amount] > 0");

            // A lump sum has no quantity and no unit price. A priced line has both, and its amount is their product.
            // Every column is tested with IS (NOT) NULL first: a CHECK passes when its result is unknown.
            table.HasCheckConstraint(
                "CK_BudgetLine_Pricing",
                "([Quantity] IS NULL AND [UnitPrice] IS NULL) "
                + "OR ([Quantity] IS NOT NULL AND [UnitPrice] IS NOT NULL AND [Quantity] > 0 AND [UnitPrice] > 0 "
                + "AND ABS([Amount] - ROUND([Quantity] * [UnitPrice], 2)) < 0.01)");
        });

        builder.Property(line => line.CostGroup).HasMaxLength(30);
        builder.Property(line => line.Description).HasMaxLength(DescriptionMaxLength).IsUnicode();
        builder.Property(line => line.Unit).HasMaxLength(20);
        builder.Property(line => line.Quantity).HasPrecision(18, 3);
        builder.Property(line => line.UnitPrice).HasPrecision(18, 2);
        builder.Property(line => line.Amount).HasPrecision(18, 2);

        builder.HasIndex(line => new { line.BudgetId, line.LineNo }, LineNoIndex).IsUnique();

        // "Which budget lines are for this item?" for the budget comparison per item.
        builder.HasIndex(line => line.ProjectItemId, "IX_BudgetLine_ProjectItemId");

        builder.HasOne(line => line.ProjectItem)
            .WithMany()
            .HasForeignKey(line => line.ProjectItemId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class DailyReportConfiguration : IEntityTypeConfiguration<DailyReport>
{
    /// <summary>One report per author, project and day. A lost race is answered with 409 <c>report_exists</c>.</summary>
    public const string AuthorDayIndex = "UX_DailyReport_Project_Date_Author";

    /// <summary>One report per author and idempotency key: a repeated offline send returns the first report.</summary>
    public const string IdempotencyKeyIndex = "UX_DailyReport_Author_IdempotencyKey";

    /// <summary>The foreign key to <c>access.User</c>. Added by hand in the migration: Access owns that table.</summary>
    public const string AuthorForeignKey = "FK_DailyReport_User_AuthorUserId";

    public const int WeatherMaxLength = 100;
    public const int TextMaxLength = 2000;
    public const int ReviewNoteMaxLength = 500;
    public const int MaxWorkersPresent = 100_000;

    public void Configure(EntityTypeBuilder<DailyReport> builder)
    {
        builder.ToTable(nameof(DailyReport), table =>
        {
            table.HasCheckConstraint("CK_DailyReport_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<DailyReportStatus>())})");
            table.HasCheckConstraint("CK_DailyReport_WorkersPresent", "[WorkersPresent] IS NULL OR [WorkersPresent] >= 0");

            // A reviewed or returned report says who decided and when. A returned one also says why.
            // A report that was returned and sent again is Submitted and keeps the last return, so Submitted
            // allows both.
            table.HasCheckConstraint(
                "CK_DailyReport_Review",
                "[Status] = 'Submitted' "
                + "OR ([Status] = 'Reviewed' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL) "
                + "OR ([Status] = 'Returned' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL AND [ReviewNote] IS NOT NULL)");
        });

        builder.Property(report => report.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(report => report.Weather).HasMaxLength(WeatherMaxLength).IsUnicode();
        builder.Property(report => report.Issues).HasMaxLength(TextMaxLength).IsUnicode();
        builder.Property(report => report.Notes).HasMaxLength(TextMaxLength).IsUnicode();
        builder.Property(report => report.ReviewNote).HasMaxLength(ReviewNoteMaxLength).IsUnicode();
        builder.Property(report => report.RowVersion).IsRowVersion();

        builder.HasIndex(report => new { report.ProjectId, report.ReportDate, report.AuthorUserId }, AuthorDayIndex).IsUnique();
        builder.HasIndex(report => new { report.AuthorUserId, report.IdempotencyKey }, IdempotencyKeyIndex)
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL");

        // The review queue of a project.
        builder.HasIndex(report => new { report.ProjectId, report.Status }, "IX_DailyReport_Project_Status");
        builder.HasIndex(report => report.ReportRequestId, "IX_DailyReport_ReportRequestId");

        builder.HasOne(report => report.Project)
            .WithMany()
            .HasForeignKey(report => report.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(report => report.ReportRequest)
            .WithMany()
            .HasForeignKey(report => report.ReportRequestId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(report => report.Lines)
            .WithOne(line => line.Report)
            .HasForeignKey(line => line.ReportId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(report => report.Materials)
            .WithOne(material => material.Report)
            .HasForeignKey(material => material.ReportId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(report => report.Equipment)
            .WithOne(equipment => equipment.Report)
            .HasForeignKey(equipment => equipment.ReportId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class DailyReportLineConfiguration : IEntityTypeConfiguration<DailyReportLine>
{
    public const string LineNoIndex = "UX_DailyReportLine_Report_LineNo";

    public const int DescriptionMaxLength = 500;
    public const int SubcontractorNameMaxLength = 200;
    public const int MaxWorkersCount = 100_000;

    public void Configure(EntityTypeBuilder<DailyReportLine> builder)
    {
        builder.ToTable(nameof(DailyReportLine), table =>
        {
            table.HasCheckConstraint("CK_DailyReportLine_WorkType", $"[WorkType] IN ({SqlList.Of(WorkTypes.All)})");
            table.HasCheckConstraint("CK_DailyReportLine_Unit", $"[Unit] IN ({SqlList.Of(MeasurementUnits.All)})");
            table.HasCheckConstraint("CK_DailyReportLine_PerformedBy", $"[PerformedBy] IN ({SqlList.Of(Enum.GetNames<WorkPerformer>())})");
            table.HasCheckConstraint("CK_DailyReportLine_LineNo", "[LineNo] >= 1");
            table.HasCheckConstraint("CK_DailyReportLine_Quantity", "[Quantity] > 0");
            table.HasCheckConstraint("CK_DailyReportLine_WorkersCount", "[WorkersCount] IS NULL OR [WorkersCount] >= 0");

            // An internal line names no subcontractor. A subcontractor line names one, by name or by contract.
            table.HasCheckConstraint(
                "CK_DailyReportLine_Subcontractor",
                "([PerformedBy] = 'Internal' AND [SubcontractorName] IS NULL AND [SubContractId] IS NULL) "
                + "OR ([PerformedBy] = 'Subcontractor' AND ([SubcontractorName] IS NOT NULL OR [SubContractId] IS NOT NULL))");
        });

        builder.Property(line => line.WorkType).HasMaxLength(30);
        builder.Property(line => line.Description).HasMaxLength(DescriptionMaxLength).IsUnicode();
        builder.Property(line => line.Unit).HasMaxLength(20);
        builder.Property(line => line.Quantity).HasPrecision(18, 3);
        builder.Property(line => line.PerformedBy).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(line => line.SubcontractorName).HasMaxLength(SubcontractorNameMaxLength).IsUnicode();

        builder.HasIndex(line => new { line.ReportId, line.LineNo }, LineNoIndex).IsUnique();
        builder.HasIndex(line => line.ProjectItemId, "IX_DailyReportLine_ProjectItemId");

        builder.HasOne(line => line.ProjectItem)
            .WithMany()
            .HasForeignKey(line => line.ProjectItemId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class DailyReportMaterialConfiguration : IEntityTypeConfiguration<DailyReportMaterial>
{
    public const int NameMaxLength = 200;

    public void Configure(EntityTypeBuilder<DailyReportMaterial> builder)
    {
        builder.ToTable(nameof(DailyReportMaterial), table =>
        {
            table.HasCheckConstraint("CK_DailyReportMaterial_Unit", $"[Unit] IN ({SqlList.Of(MeasurementUnits.All)})");
            table.HasCheckConstraint("CK_DailyReportMaterial_Quantity", "[Quantity] > 0");
        });

        builder.Property(material => material.MaterialName).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(material => material.Unit).HasMaxLength(20);
        builder.Property(material => material.Quantity).HasPrecision(18, 3);

        builder.HasIndex(material => material.ReportId, "IX_DailyReportMaterial_ReportId");
    }
}

internal sealed class DailyReportEquipmentConfiguration : IEntityTypeConfiguration<DailyReportEquipment>
{
    public const int NameMaxLength = 200;
    public const decimal MaxHours = 24m;

    public void Configure(EntityTypeBuilder<DailyReportEquipment> builder)
    {
        builder.ToTable(nameof(DailyReportEquipment), table =>
        {
            table.HasCheckConstraint("CK_DailyReportEquipment_Hours", "[Hours] IS NULL OR ([Hours] > 0 AND [Hours] <= 24)");
        });

        builder.Property(equipment => equipment.EquipmentName).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(equipment => equipment.Hours).HasPrecision(5, 1);

        builder.HasIndex(equipment => equipment.ReportId, "IX_DailyReportEquipment_ReportId");
    }
}

internal sealed class ReportRequestConfiguration : IEntityTypeConfiguration<ReportRequest>
{
    /// <summary>The foreign keys to <c>access.User</c>. Added by hand in the migration: Access owns that table.</summary>
    public const string RequesterForeignKey = "FK_ReportRequest_User_RequesterUserId";
    public const string RecipientForeignKey = "FK_ReportRequest_User_RecipientUserId";

    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int ResponseMaxLength = 4000;
    public const int NoteMaxLength = 500;

    public void Configure(EntityTypeBuilder<ReportRequest> builder)
    {
        builder.ToTable(nameof(ReportRequest), table =>
        {
            table.HasCheckConstraint("CK_ReportRequest_Type", $"[Type] IN ({SqlList.Of(Enum.GetNames<ReportRequestType>())})");
            table.HasCheckConstraint("CK_ReportRequest_Priority", $"[Priority] IN ({SqlList.Of(Enum.GetNames<ReportRequestPriority>())})");
            table.HasCheckConstraint("CK_ReportRequest_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<ReportRequestStatus>())})");

            // Nobody asks themselves, and nobody reviews their own answer.
            table.HasCheckConstraint("CK_ReportRequest_Parties", "[RequesterUserId] <> [RecipientUserId]");

            // Each status carries the facts that led to it. Every column is tested with IS NOT NULL: a CHECK
            // passes when its result is unknown.
            table.HasCheckConstraint(
                "CK_ReportRequest_Answer",
                "[Status] NOT IN ('Submitted', 'Completed') OR ([SubmittedAt] IS NOT NULL AND [ResponseText] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_ReportRequest_Review",
                "[Status] NOT IN ('Completed', 'Returned') OR ([ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL)");
            table.HasCheckConstraint("CK_ReportRequest_Return", "[Status] <> 'Returned' OR [ReturnNote] IS NOT NULL");
            table.HasCheckConstraint(
                "CK_ReportRequest_Cancel",
                "[Status] <> 'Cancelled' OR ([CancelledAt] IS NOT NULL AND [CancelNote] IS NOT NULL)");

            // A report nobody asked for always has its text: it is created with it.
            table.HasCheckConstraint("CK_ReportRequest_Proactive", "[IsProactive] = 0 OR [ResponseText] IS NOT NULL");
        });

        builder.Property(request => request.Type).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(request => request.Priority).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(request => request.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(request => request.Title).HasMaxLength(TitleMaxLength).IsUnicode();
        builder.Property(request => request.Description).HasMaxLength(DescriptionMaxLength).IsUnicode();
        builder.Property(request => request.ResponseText).HasMaxLength(ResponseMaxLength).IsUnicode();
        builder.Property(request => request.ReturnNote).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(request => request.CancelNote).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(request => request.RowVersion).IsRowVersion();

        // The requests of a project by status, and "what is waiting for me?".
        builder.HasIndex(request => new { request.ProjectId, request.Status }, "IX_ReportRequest_Project_Status");
        builder.HasIndex(request => request.RecipientUserId, "IX_ReportRequest_RecipientUserId");
        builder.HasIndex(request => request.RequesterUserId, "IX_ReportRequest_RequesterUserId");

        builder.HasOne(request => request.Project)
            .WithMany()
            .HasForeignKey(request => request.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
