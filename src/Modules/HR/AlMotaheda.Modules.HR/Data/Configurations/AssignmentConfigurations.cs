using AlMotaheda.Modules.HR.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.HR.Data.Configurations;

internal sealed class EmployeeAssignmentConfiguration : IEntityTypeConfiguration<EmployeeAssignment>
{
    /// <summary>One open stay per employee and project (409 <c>already_assigned</c>).</summary>
    public const string OpenIndex = "UX_EmployeeAssignment_Open";

    /// <summary>Written by hand in the migration: the project is a <c>projects.Project</c>.</summary>
    public const string ProjectForeignKey = "FK_EmployeeAssignment_Project_ProjectId";

    public const int NotesMaxLength = 500;

    public void Configure(EntityTypeBuilder<EmployeeAssignment> builder)
    {
        builder.ToTable(nameof(EmployeeAssignment), table =>
        {
            table.HasCheckConstraint("CK_EmployeeAssignment_Role", $"[Role] IN ({SqlList.Of(Enum.GetNames<EmployeeAssignmentRole>())})");
            table.HasCheckConstraint("CK_EmployeeAssignment_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
            table.HasCheckConstraint(
                "CK_EmployeeAssignment_End",
                "([EndDate] IS NULL AND [EndedAt] IS NULL AND [EndedBy] IS NULL) OR ([EndDate] IS NOT NULL AND [EndedAt] IS NOT NULL AND [EndedBy] IS NOT NULL)");
        });

        builder.Property(assignment => assignment.Role).HasConversion<string>().HasMaxLength(30);
        builder.Property(assignment => assignment.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(assignment => assignment.EndReason).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(assignment => assignment.RowVersion).IsRowVersion();

        builder.HasIndex(assignment => new { assignment.EmployeeId, assignment.ProjectId }).IsUnique().HasFilter("[EndDate] IS NULL").HasDatabaseName(OpenIndex);
        builder.HasIndex(assignment => new { assignment.ProjectId, assignment.StartDate }, "IX_EmployeeAssignment_Project_Start");
        builder.HasIndex(assignment => new { assignment.EmployeeId, assignment.StartDate }, "IX_EmployeeAssignment_Employee_Start");

        builder.HasOne<Employee>().WithMany().HasForeignKey(assignment => assignment.EmployeeId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class HrReferralConfiguration : IEntityTypeConfiguration<HrReferral>
{
    /// <summary>A record is referred once.</summary>
    public const string SourceIndex = "UX_HrReferral_Source";

    public const int SubjectMaxLength = 200;
    public const int DetailsMaxLength = 2000;
    public const int NoteMaxLength = 1000;
    public const int ReferenceMaxLength = 50;

    public void Configure(EntityTypeBuilder<HrReferral> builder)
    {
        builder.ToTable(nameof(HrReferral), table =>
        {
            table.HasCheckConstraint("CK_HrReferral_SourceType", $"[SourceType] IN ({SqlList.Of(HrReferralSources.All)})");
            table.HasCheckConstraint("CK_HrReferral_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<HrReferralStatus>())})");
            table.HasCheckConstraint("CK_HrReferral_Outcome", $"[Outcome] IS NULL OR [Outcome] IN ({SqlList.Of(Enum.GetNames<HrReferralOutcome>())})");
            table.HasCheckConstraint("CK_HrReferral_Amount", "[Amount] IS NULL OR [Amount] > 0");

            // A closed referral has its outcome; a deduction is recorded only with the outcome that says so.
            table.HasCheckConstraint(
                "CK_HrReferral_Closed",
                "([Status] <> 'Closed' AND [Outcome] IS NULL AND [ClosedAt] IS NULL AND [ClosedBy] IS NULL) "
                + "OR ([Status] = 'Closed' AND [Outcome] IS NOT NULL AND [ClosedAt] IS NOT NULL AND [ClosedBy] IS NOT NULL)");
            table.HasCheckConstraint("CK_HrReferral_Deduction", "([Outcome] = 'DeductionApplied' AND [DeductionInputId] IS NOT NULL) OR ([Outcome] IS NULL OR [Outcome] <> 'DeductionApplied') AND [DeductionInputId] IS NULL");
        });

        builder.Property(referral => referral.Number)
            .HasMaxLength(20)
            .HasComputedColumnSql("CONCAT('HRR-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true);
        builder.HasIndex(referral => referral.Number).IsUnique().HasDatabaseName("UX_HrReferral_Number");

        builder.Property(referral => referral.SourceType).HasMaxLength(30);
        builder.Property(referral => referral.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(referral => referral.Outcome).HasConversion<string>().HasMaxLength(30);
        builder.Property(referral => referral.Subject).HasMaxLength(SubjectMaxLength).IsUnicode();
        builder.Property(referral => referral.Details).HasMaxLength(DetailsMaxLength).IsUnicode();
        builder.Property(referral => referral.ReferenceNo).HasMaxLength(ReferenceMaxLength).IsUnicode();
        builder.Property(referral => referral.InvestigationNote).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(referral => referral.ClosingNote).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(referral => referral.RowVersion).IsRowVersion();

        builder.HasIndex(referral => new { referral.SourceType, referral.SourceId }).IsUnique().HasDatabaseName(SourceIndex);
        builder.HasIndex(referral => new { referral.Status, referral.Id }, "IX_HrReferral_Status");
        builder.HasIndex(referral => referral.EmployeeId, "IX_HrReferral_EmployeeId");

        builder.HasOne<Employee>().WithMany().HasForeignKey(referral => referral.EmployeeId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<PayrollInput>().WithMany().HasForeignKey(referral => referral.DeductionInputId).OnDelete(DeleteBehavior.NoAction);
    }
}
