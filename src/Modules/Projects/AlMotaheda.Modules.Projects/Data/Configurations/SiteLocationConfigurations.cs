using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Projects.Data.Configurations;

internal sealed class LocationSharingConfiguration : IEntityTypeConfiguration<LocationSharing>
{
    /// <summary>The foreign key to <c>access.User</c>. Added by hand in the migration: Access owns that table.</summary>
    public const string UserForeignKey = "FK_LocationSharing_User_UserId";

    public void Configure(EntityTypeBuilder<LocationSharing> builder)
    {
        builder.ToTable(nameof(LocationSharing), table =>
        {
            table.HasCheckConstraint(
                "CK_LocationSharing_Position",
                "([LastLatitude] IS NULL AND [LastLongitude] IS NULL) OR ([LastLatitude] BETWEEN -90 AND 90 AND [LastLongitude] BETWEEN -180 AND 180)");
            table.HasCheckConstraint("CK_LocationSharing_On", "[IsOn] = 0 OR [TurnedOnAt] IS NOT NULL");
        });

        builder.Property(sharing => sharing.LastLatitude).HasPrecision(9, 6);
        builder.Property(sharing => sharing.LastLongitude).HasPrecision(9, 6);
        builder.Property(sharing => sharing.RowVersion).IsRowVersion();

        // One row per engineer and project. The monitor reads the rows that are on.
        builder.HasIndex(sharing => new { sharing.ProjectId, sharing.UserId }, "UX_LocationSharing_Project_User").IsUnique();
        builder.HasIndex(sharing => sharing.IsOn, "IX_LocationSharing_On").HasFilter("[IsOn] = 1");

        builder.HasOne(sharing => sharing.Project)
            .WithMany()
            .HasForeignKey(sharing => sharing.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class LocationPingConfiguration : IEntityTypeConfiguration<LocationPing>
{
    public const string ClientPointIndex = "UX_LocationPing_User_ClientPoint_Project";

    public void Configure(EntityTypeBuilder<LocationPing> builder)
    {
        builder.ToTable(nameof(LocationPing), table =>
            table.HasCheckConstraint(
                "CK_LocationPing_Position",
                "[Latitude] BETWEEN -90 AND 90 AND [Longitude] BETWEEN -180 AND 180 AND ([AccuracyMeters] IS NULL OR [AccuracyMeters] >= 0)"));

        builder.Property(ping => ping.Latitude).HasPrecision(9, 6);
        builder.Property(ping => ping.Longitude).HasPrecision(9, 6);

        // One engineer's day on one project, and the daily clean-up of old positions.
        builder.HasIndex(ping => new { ping.ProjectId, ping.UserId, ping.RecordedAt }, "IX_LocationPing_Project_User_RecordedAt");

        // A queued position sent twice is stored once per project.
        builder.HasIndex(ping => new { ping.UserId, ping.ClientPointId, ping.ProjectId }, ClientPointIndex)
            .IsUnique()
            .HasFilter("[ClientPointId] IS NOT NULL");
        builder.HasIndex(ping => ping.ReceivedAt, "IX_LocationPing_ReceivedAt");

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(ping => ping.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class LocationAlertConfiguration : IEntityTypeConfiguration<LocationAlert>
{
    public const string EngineerForeignKey = "FK_LocationAlert_User_EngineerUserId";
    public const string RecipientForeignKey = "FK_LocationAlert_User_RecipientUserId";

    /// <summary>The kind of alert is its class, stored in this column.</summary>
    public const string KindColumn = "Kind";
    public const string Unavailable = "Unavailable";
    public const string OutsideSite = "OutsideSite";
    public const string LowBattery = "LowBattery";
    private static readonly string[] Kinds = [Unavailable, OutsideSite, LowBattery];

    public void Configure(EntityTypeBuilder<LocationAlert> builder)
    {
        builder.ToTable(nameof(LocationAlert), table =>
        {
            table.HasCheckConstraint("CK_LocationAlert_Kind", $"[Kind] IN ({SqlList.Of(Kinds)})");
            table.HasCheckConstraint("CK_LocationAlert_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<LocationAlertStatus>())})");
            table.HasCheckConstraint("CK_LocationAlert_Ended", "([Status] = 'Open' AND [EndedAt] IS NULL) OR ([Status] <> 'Open' AND [EndedAt] IS NOT NULL)");
            table.HasCheckConstraint("CK_LocationAlert_Parties", "[EngineerUserId] <> [RecipientUserId]");
        });

        builder.HasDiscriminator<string>(KindColumn)
            .HasValue<LocationUnavailableAlert>(Unavailable)
            .HasValue<OutsideSiteAlert>(OutsideSite)
            .HasValue<LowBatteryAlert>(LowBattery);
        builder.Property<string>(KindColumn).HasMaxLength(30).IsUnicode(false);
        builder.Property(alert => alert.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(alert => alert.RowVersion).IsRowVersion();

        // At most one open alert of a kind per engineer, project and recipient: one outage notifies once.
        builder.HasIndex([nameof(LocationAlert.ProjectId), nameof(LocationAlert.EngineerUserId), KindColumn, nameof(LocationAlert.RecipientUserId)], "UX_LocationAlert_Open")
            .IsUnique()
            .HasFilter("[Status] = 'Open'");
        builder.HasIndex(alert => new { alert.ProjectId, alert.StartedAt }, "IX_LocationAlert_Project_StartedAt");

        builder.HasOne(alert => alert.Project)
            .WithMany()
            .HasForeignKey(alert => alert.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class LocationDeviceConfiguration : IEntityTypeConfiguration<LocationDevice>
{
    /// <summary>The foreign key to <c>access.User</c>. Added by hand in the migration: Access owns that table.</summary>
    public const string UserForeignKey = "FK_LocationDevice_User_UserId";

    public const string UserDeviceIndex = "UX_LocationDevice_User_Device";

    public const int DeviceIdMaxLength = 64;
    public const int PlatformMaxLength = 20;
    public const int AppVersionMaxLength = 40;
    public const int StateMaxLength = 30;

    public static readonly string[] TrackingStates = ["Running", "PermissionDenied", "BackgroundDenied", "GpsOff", "Stopped"];
    public static readonly string[] Permissions = ["Always", "WhileInUse", "Denied"];

    public void Configure(EntityTypeBuilder<LocationDevice> builder)
    {
        builder.ToTable(nameof(LocationDevice), table =>
        {
            table.HasCheckConstraint("CK_LocationDevice_Battery", "[BatteryPercent] IS NULL OR [BatteryPercent] BETWEEN 0 AND 100");
            table.HasCheckConstraint("CK_LocationDevice_TrackingState", $"[TrackingState] IS NULL OR [TrackingState] IN ({SqlList.Of(TrackingStates)})");
            table.HasCheckConstraint("CK_LocationDevice_Permission", $"[Permission] IS NULL OR [Permission] IN ({SqlList.Of(Permissions)})");
            table.HasCheckConstraint("CK_LocationDevice_PendingPoints", "[PendingPoints] IS NULL OR [PendingPoints] >= 0");
        });

        builder.Property(device => device.DeviceId).HasMaxLength(DeviceIdMaxLength).IsUnicode(false);
        builder.Property(device => device.Platform).HasMaxLength(PlatformMaxLength).IsUnicode(false);
        builder.Property(device => device.AppVersion).HasMaxLength(AppVersionMaxLength).IsUnicode(false);
        builder.Property(device => device.TrackingState).HasMaxLength(StateMaxLength).IsUnicode(false);
        builder.Property(device => device.Permission).HasMaxLength(StateMaxLength).IsUnicode(false);
        builder.Property(device => device.RowVersion).IsRowVersion();

        builder.HasIndex(device => new { device.UserId, device.DeviceId }, UserDeviceIndex).IsUnique();
    }
}
