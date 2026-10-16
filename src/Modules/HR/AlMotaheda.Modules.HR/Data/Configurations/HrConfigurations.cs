using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.HR.Data.Configurations;

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public const string NameArIndex = "UX_Department_NameAr";
    public const string NameEnIndex = "UX_Department_NameEn";
    public const int NameMaxLength = 100;

    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable(nameof(Department));

        builder.Property(department => department.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(department => department.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(department => department.RowVersion).IsRowVersion();

        builder.HasIndex(department => department.NameAr).IsUnique().HasDatabaseName(NameArIndex);
        builder.HasIndex(department => department.NameEn).IsUnique().HasDatabaseName(NameEnIndex);
    }
}

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public const string NationalIdIndex = "UX_Employee_NationalId";
    public const string UserIndex = "UX_Employee_UserId";

    public const int NameMaxLength = 150;
    public const int NationalIdLength = 14;
    public const int PhoneMaxLength = 30;
    public const int EmailMaxLength = 200;
    public const int JobTitleMaxLength = 100;
    public const int ReasonMaxLength = 500;
    public const int BankNameMaxLength = 100;
    public const int IbanMaxLength = 34;
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable(nameof(Employee), table =>
        {
            table.HasCheckConstraint("CK_Employee_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<EmployeeKind>())})");
            table.HasCheckConstraint("CK_Employee_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<EmployeeStatus>())})");
            // An ended employee always has an end date, on or after the hiring date.
            table.HasCheckConstraint("CK_Employee_Ended",
                "([Status] = 'Active' AND [EndDate] IS NULL) OR ([Status] = 'Ended' AND [EndDate] IS NOT NULL AND [EndDate] >= [HireDate])");
            table.HasCheckConstraint("CK_Employee_NationalId", "LEN([NationalId]) = 14 AND [NationalId] NOT LIKE '%[^0-9]%'");
        });

        // EMP-000123: deterministic, so it is stored and indexed.
        builder.Property(employee => employee.EmployeeNo)
            .HasMaxLength(20)
            .HasComputedColumnSql("CONCAT('EMP-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true);
        builder.HasIndex(employee => employee.EmployeeNo).IsUnique().HasDatabaseName("UX_Employee_EmployeeNo");

        builder.Property(employee => employee.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(employee => employee.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(employee => employee.NationalId).HasMaxLength(NationalIdLength).IsFixedLength();
        builder.Property(employee => employee.Phone).HasMaxLength(PhoneMaxLength);
        builder.Property(employee => employee.Email).HasMaxLength(EmailMaxLength);
        builder.Property(employee => employee.JobTitle).HasMaxLength(JobTitleMaxLength).IsUnicode();
        builder.Property(employee => employee.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(employee => employee.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(employee => employee.EndReason).HasMaxLength(ReasonMaxLength).IsUnicode();
        builder.Property(employee => employee.BankName).HasMaxLength(BankNameMaxLength).IsUnicode();
        builder.Property(employee => employee.Iban).HasMaxLength(IbanMaxLength);
        builder.Property(employee => employee.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(employee => employee.RowVersion).IsRowVersion();

        builder.HasIndex(employee => employee.NationalId).IsUnique().HasDatabaseName(NationalIdIndex);
        builder.HasIndex(employee => employee.UserId).IsUnique().HasFilter("[UserId] IS NOT NULL").HasDatabaseName(UserIndex);
        builder.HasIndex(employee => new { employee.Status, employee.NameEn }, "IX_Employee_Status_Name");

        builder.HasOne<Department>().WithMany().HasForeignKey(employee => employee.DepartmentId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class EmployeeDocumentConfiguration : IEntityTypeConfiguration<EmployeeDocument>
{
    public const int NumberMaxLength = 50;

    public void Configure(EntityTypeBuilder<EmployeeDocument> builder)
    {
        builder.ToTable(nameof(EmployeeDocument), table =>
        {
            table.HasCheckConstraint("CK_EmployeeDocument_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<EmployeeDocumentKind>())})");
            table.HasCheckConstraint("CK_EmployeeDocument_Dates", "[IssueDate] IS NULL OR [ExpiryDate] IS NULL OR [ExpiryDate] >= [IssueDate]");
        });

        builder.Property(document => document.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(document => document.Number).HasMaxLength(NumberMaxLength).IsUnicode();
        builder.Property(document => document.Notes).HasMaxLength(EmployeeConfiguration.NotesMaxLength).IsUnicode();
        builder.Property(document => document.RowVersion).IsRowVersion();

        builder.HasIndex(document => document.EmployeeId, "IX_EmployeeDocument_EmployeeId");
        // "Which documents expire soon?"
        builder.HasIndex(document => document.ExpiryDate, "IX_EmployeeDocument_ExpiryDate").HasFilter("[ExpiryDate] IS NOT NULL");

        builder.HasOne<Employee>().WithMany().HasForeignKey(document => document.EmployeeId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class SalaryStructureConfiguration : IEntityTypeConfiguration<SalaryStructure>
{
    public const string EffectiveIndex = "UX_SalaryStructure_Employee_EffectiveFrom";
    public const int NoteMaxLength = 500;

    public void Configure(EntityTypeBuilder<SalaryStructure> builder)
    {
        builder.ToTable(nameof(SalaryStructure), table =>
        {
            table.HasCheckConstraint("CK_SalaryStructure_Basic", "[BasicSalary] > 0");
            table.HasCheckConstraint("CK_SalaryStructure_Allowance", "[FixedAllowance] >= 0");
        });

        builder.Property(structure => structure.Note).HasMaxLength(NoteMaxLength).IsUnicode();

        builder.HasIndex(structure => new { structure.EmployeeId, structure.EffectiveFrom }).IsUnique().HasDatabaseName(EffectiveIndex);

        builder.HasOne<Employee>().WithMany().HasForeignKey(structure => structure.EmployeeId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PayrollInputConfiguration : IEntityTypeConfiguration<PayrollInput>
{
    public const int ReasonMaxLength = 500;

    public void Configure(EntityTypeBuilder<PayrollInput> builder)
    {
        builder.ToTable(nameof(PayrollInput), table =>
        {
            table.HasCheckConstraint("CK_PayrollInput_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<PayrollInputKind>())})");
            table.HasCheckConstraint("CK_PayrollInput_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<PayrollInputStatus>())})");
            table.HasCheckConstraint("CK_PayrollInput_Amount", "[Amount] > 0");
            table.HasCheckConstraint("CK_PayrollInput_Month", "DAY([Month]) = 1");
        });

        builder.Property(input => input.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(input => input.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(input => input.Reason).HasMaxLength(ReasonMaxLength).IsUnicode();
        builder.Property(input => input.CancelReason).HasMaxLength(ReasonMaxLength).IsUnicode();
        builder.Property(input => input.RowVersion).IsRowVersion();

        builder.HasIndex(input => new { input.Month, input.EmployeeId }, "IX_PayrollInput_Month_Employee");

        builder.HasOne<Employee>().WithMany().HasForeignKey(input => input.EmployeeId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class SalarySheetConfiguration : IEntityTypeConfiguration<SalarySheet>
{
    public const string MonthIndex = "UX_SalarySheet_Month";
    public const int NoteMaxLength = 1000;

    public void Configure(EntityTypeBuilder<SalarySheet> builder)
    {
        builder.ToTable(nameof(SalarySheet), table =>
        {
            table.HasCheckConstraint("CK_SalarySheet_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<SalarySheetStatus>())})");
            table.HasCheckConstraint("CK_SalarySheet_Month", "DAY([Month]) = 1");
            table.HasCheckConstraint("CK_SalarySheet_Total", "[Total] >= 0");
            table.HasCheckConstraint("CK_SalarySheet_Paid",
                "[Status] <> 'Paid' OR ([PaidOn] IS NOT NULL AND [PaymentRequestId] IS NOT NULL)");
        });

        builder.Property(sheet => sheet.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(sheet => sheet.ReviewNote).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(sheet => sheet.RowVersion).IsRowVersion();

        builder.HasIndex(sheet => sheet.Month).IsUnique().HasDatabaseName(MonthIndex);

        // Lines are replaced on recalculation: removed lines are deleted by EF, never by the database.
        builder.HasMany(sheet => sheet.Lines).WithOne().HasForeignKey(line => line.SheetId).OnDelete(DeleteBehavior.ClientCascade);
    }
}

internal sealed class SalarySheetLineConfiguration : IEntityTypeConfiguration<SalarySheetLine>
{
    public void Configure(EntityTypeBuilder<SalarySheetLine> builder)
    {
        builder.ToTable(nameof(SalarySheetLine), table =>
        {
            table.HasCheckConstraint("CK_SalarySheetLine_Net", "[NetSalary] >= 0");
            table.HasCheckConstraint("CK_SalarySheetLine_Sum",
                "[NetSalary] = [BasicSalary] + [FixedAllowance] + [Bonuses] - [Deductions] - [AdvanceRecoveries]");
        });

        builder.Property(line => line.EmployeeNo).HasMaxLength(20);
        builder.Property(line => line.NameAr).HasMaxLength(EmployeeConfiguration.NameMaxLength).IsUnicode();
        builder.Property(line => line.NameEn).HasMaxLength(EmployeeConfiguration.NameMaxLength).IsUnicode();

        builder.HasIndex(line => new { line.SheetId, line.EmployeeId }).IsUnique().HasDatabaseName("UX_SalarySheetLine_Employee");
        builder.HasIndex(line => line.EmployeeId, "IX_SalarySheetLine_EmployeeId");

        builder.HasOne<Employee>().WithMany().HasForeignKey(line => line.EmployeeId).OnDelete(DeleteBehavior.NoAction);
    }
}
