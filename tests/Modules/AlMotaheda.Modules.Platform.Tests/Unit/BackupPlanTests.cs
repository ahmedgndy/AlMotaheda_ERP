using AlMotaheda.Modules.Platform.DatabaseBackup;

namespace AlMotaheda.Modules.Platform.Tests.Unit;

/// <summary>The backup rules: names, the daily schedule, and what retention may remove.</summary>
public sealed class BackupPlanTests
{
    private static readonly DateTime At = new(2026, 10, 16, 2, 0, 5);

    [Fact]
    public void Files_have_one_strict_name_and_only_those_names_are_ever_removed()
    {
        Assert.Equal("AlMotahedaErp_Full_20261016_020005.bak", BackupPlan.BackupFileName("AlMotahedaErp", At));
        Assert.Equal("AlMotahedaErp_Files_20261016_020005.zip", BackupPlan.DocumentsFileName("AlMotahedaErp", At));

        Assert.True(BackupPlan.IsServiceFile("AlMotahedaErp_Full_20261016_020005.bak", "AlMotahedaErp"));
        Assert.False(BackupPlan.IsServiceFile("AlMotahedaErp_Full_20261016_020005.bak", "OtherDb"));
        Assert.False(BackupPlan.IsServiceFile("AlMotahedaErp_manual.bak", "AlMotahedaErp"));
        Assert.False(BackupPlan.IsServiceFile("AlMotahedaErp_Full_20261016_020005.bak.old", "AlMotahedaErp"));
        Assert.False(BackupPlan.IsServiceFile("..\\AlMotahedaErp_Full_20261016_020005.bak", "AlMotahedaErp"));
    }

    [Theory]
    [InlineData(true, "01:59", null, false)]                  // before the time
    [InlineData(true, "02:00", null, true)]
    [InlineData(true, "23:00", "2026-10-16 02:00", false)]    // already ran today
    [InlineData(true, "02:01", "2026-10-15 02:00", true)]     // ran yesterday
    [InlineData(false, "03:00", null, false)]                 // turned off
    public void The_daily_backup_runs_once_a_day_at_or_after_its_time(bool enabled, string now, string? lastRun, bool due)
    {
        var settings = new BackupSettings { Enabled = enabled, DailyAt = new TimeOnly(2, 0) };
        var nowLocal = DateTime.Parse($"2026-10-16 {now}", System.Globalization.CultureInfo.InvariantCulture);
        DateTime? last = lastRun is null ? null : DateTime.Parse(lastRun, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(due, BackupPlan.IsDue(settings, nowLocal, last));
    }

    [Fact]
    public void Retention_keeps_the_newest_verified_backups_and_never_counts_failed_ones()
    {
        BackupRun Run(long id, int day, BackupStatus status = BackupStatus.Succeeded, bool verified = true) =>
            new() { Id = id, StartedAt = new DateTime(2026, 10, day), Status = status, Verified = verified };
        var runs = new[] { Run(1, 10), Run(2, 11), Run(3, 12, BackupStatus.Failed, false), Run(4, 13), Run(5, 14), Run(6, 15, BackupStatus.Succeeded, verified: false) };

        Assert.Equal([2L, 1L], BackupPlan.ToRemove(runs, keep: 2).Select(run => run.Id));
        Assert.Equal([1L], BackupPlan.ToRemove(runs, keep: 3).Select(run => run.Id));
        Assert.Equal([4L, 2L, 1L], BackupPlan.ToRemove(runs, keep: 0).Select(run => run.Id)); // the newest always stays
    }
}
