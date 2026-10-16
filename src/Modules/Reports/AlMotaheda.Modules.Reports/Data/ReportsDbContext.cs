using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Reports.Data;

/// <summary>The Reports module's table, in the <c>rpt</c> schema. Report data itself stays in the owning modules.</summary>
internal sealed class ReportsDbContext(DbContextOptions<ReportsDbContext> options) : DbContext(options)
{
    public const string Schema = "rpt";

    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DbSet<ReportRun> Runs => Set<ReportRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReportsDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        StorageConventions.Apply(configurationBuilder);
}

internal sealed class ReportRunConfiguration : IEntityTypeConfiguration<ReportRun>
{
    public void Configure(EntityTypeBuilder<ReportRun> builder)
    {
        builder.ToTable(nameof(ReportRun), table =>
        {
            table.HasCheckConstraint("CK_ReportRun_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<ReportRunKind>())})");
            table.HasCheckConstraint("CK_ReportRun_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<ReportRunStatus>())})");
            table.HasCheckConstraint("CK_ReportRun_Language", "[Language] IN ('ar', 'en')");
        });

        builder.Property(run => run.ReportCode).HasMaxLength(30);
        builder.Property(run => run.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(run => run.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(run => run.Language).HasMaxLength(2);
        builder.Property(run => run.Filters).HasMaxLength(2000).IsUnicode();
        builder.Property(run => run.FileName).HasMaxLength(200).IsUnicode();
        builder.Property(run => run.Error).HasMaxLength(1000).IsUnicode();
        builder.Property(run => run.Claims).IsUnicode();
        builder.Property(run => run.RowVersion).IsRowVersion();

        // The background worker's queue, and "my exports".
        builder.HasIndex(run => new { run.Status, run.Id }, "IX_ReportRun_Status");
        builder.HasIndex(run => new { run.RequestedBy, run.Id }, "IX_ReportRun_RequestedBy");
    }
}
