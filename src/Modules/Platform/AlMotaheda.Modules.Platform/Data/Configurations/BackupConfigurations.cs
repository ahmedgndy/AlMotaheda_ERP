using AlMotaheda.Modules.Platform.DatabaseBackup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Platform.Data.Configurations;

internal sealed class BackupSettingsConfiguration : IEntityTypeConfiguration<BackupSettings>
{
    public void Configure(EntityTypeBuilder<BackupSettings> builder)
    {
        builder.ToTable(nameof(BackupSettings), table =>
        {
            table.HasCheckConstraint("CK_BackupSettings_Single", $"[Id] = {BackupSettings.SingletonId}");
            table.HasCheckConstraint("CK_BackupSettings_Retention", "[RetentionCount] BETWEEN 1 AND 365");
        });

        builder.Property(settings => settings.Id).ValueGeneratedNever();
        builder.Property(settings => settings.Folder).HasMaxLength(260).IsUnicode();
        builder.Property(settings => settings.RowVersion).IsRowVersion();

        // Daily at 02:00, keep 3 (the brief's default); the Executive sets the folder on the server.
        builder.HasData(new BackupSettings { Id = BackupSettings.SingletonId, Enabled = true, DailyAt = new TimeOnly(2, 0), Folder = BackupSettings.DefaultFolder, RetentionCount = 3, IncludeDocuments = true });
    }
}

internal sealed class BackupRunConfiguration : IEntityTypeConfiguration<BackupRun>
{
    public void Configure(EntityTypeBuilder<BackupRun> builder)
    {
        builder.ToTable(nameof(BackupRun), table =>
        {
            table.HasCheckConstraint("CK_BackupRun_Trigger", $"[Trigger] IN ({SqlList.Of(Enum.GetNames<BackupTrigger>())})");
            table.HasCheckConstraint("CK_BackupRun_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<BackupStatus>())})");
        });

        builder.Property(run => run.Trigger).HasConversion<string>().HasMaxLength(30);
        builder.Property(run => run.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(run => run.Database).HasMaxLength(128);
        builder.Property(run => run.FilePath).HasMaxLength(400).IsUnicode();
        builder.Property(run => run.DocumentsFilePath).HasMaxLength(400).IsUnicode();
        builder.Property(run => run.Error).HasMaxLength(1000).IsUnicode();

        builder.HasIndex(run => new { run.Trigger, run.StartedAt }, "IX_BackupRun_Trigger_StartedAt");
    }
}
