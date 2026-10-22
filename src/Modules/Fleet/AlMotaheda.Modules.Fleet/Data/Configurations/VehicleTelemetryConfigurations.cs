using AlMotaheda.BuildingBlocks.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Fleet.Data.Configurations;

internal sealed class VehicleGpsPointConfiguration : IEntityTypeConfiguration<VehicleGpsPoint>
{
    /// <summary>A queued position sent twice is stored once.</summary>
    public const string ClientPointIndex = "UX_VehicleGpsPoint_User_ClientPoint";

    public void Configure(EntityTypeBuilder<VehicleGpsPoint> builder)
    {
        builder.ToTable(nameof(VehicleGpsPoint), table =>
        {
            table.HasCheckConstraint("CK_VehicleGpsPoint_Position", "[Latitude] BETWEEN -90 AND 90 AND [Longitude] BETWEEN -180 AND 180");
            table.HasCheckConstraint("CK_VehicleGpsPoint_Accuracy", "[AccuracyMeters] IS NULL OR [AccuracyMeters] >= 0");
            table.HasCheckConstraint("CK_VehicleGpsPoint_Speed", "[SpeedKmh] IS NULL OR [SpeedKmh] >= 0");
            table.HasCheckConstraint("CK_VehicleGpsPoint_Heading", "[HeadingDegrees] IS NULL OR [HeadingDegrees] BETWEEN 0 AND 360");
        });

        builder.Property(point => point.Latitude).HasPrecision(9, 6);
        builder.Property(point => point.Longitude).HasPrecision(9, 6);
        builder.Property(point => point.SpeedKmh).HasPrecision(6, 1);
        builder.Property(point => point.AltitudeMeters).HasPrecision(7, 1);

        // One vehicle's day (route history), and the retention clean-up.
        builder.HasIndex(point => new { point.VehicleId, point.RecordedAt }, "IX_VehicleGpsPoint_Vehicle_RecordedAt");
        builder.HasIndex(point => point.RecordedAt, "IX_VehicleGpsPoint_RecordedAt");
        builder.HasIndex(point => new { point.UserId, point.ClientPointId }, ClientPointIndex).IsUnique();

        builder.HasOne<Vehicle>().WithMany().HasForeignKey(point => point.VehicleId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<VehicleDriverAssignment>().WithMany().HasForeignKey(point => point.AssignmentId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<VehicleTrackingDevice>().WithMany().HasForeignKey(point => point.DeviceId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class VehicleTrackingDeviceConfiguration : IEntityTypeConfiguration<VehicleTrackingDevice>
{
    public const string UserDeviceIndex = "UX_VehicleTrackingDevice_User_Device";

    /// <summary>The foreign key to <c>access.User</c>. Added by hand in the migration: Access owns that table.</summary>
    public const string UserForeignKey = "FK_VehicleTrackingDevice_User_UserId";
    public const int DeviceIdMaxLength = 64;
    public const int AppVersionMaxLength = 40;

    public void Configure(EntityTypeBuilder<VehicleTrackingDevice> builder)
    {
        builder.ToTable(nameof(VehicleTrackingDevice), table =>
        {
            table.HasCheckConstraint("CK_VehicleTrackingDevice_Battery", "[BatteryPercent] IS NULL OR [BatteryPercent] BETWEEN 0 AND 100");
            table.HasCheckConstraint("CK_VehicleTrackingDevice_Pending", "[PendingPoints] IS NULL OR [PendingPoints] >= 0");
        });

        builder.Property(device => device.DeviceId).HasMaxLength(DeviceIdMaxLength).IsUnicode(false);
        builder.Property(device => device.Platform).HasMaxLength(20).IsUnicode(false);
        builder.Property(device => device.AppVersion).HasMaxLength(AppVersionMaxLength).IsUnicode(false);
        builder.Property(device => device.TrackingState).HasMaxLength(30).IsUnicode(false);
        builder.Property(device => device.Permission).HasMaxLength(20).IsUnicode(false);
        builder.Property(device => device.RowVersion).IsRowVersion();

        builder.HasIndex(device => new { device.UserId, device.DeviceId }, UserDeviceIndex).IsUnique();
    }
}

internal sealed class VehicleTrackingStateConfiguration : IEntityTypeConfiguration<VehicleTrackingState>
{
    public const string PrimaryKey = "PK_VehicleTrackingState";

    public void Configure(EntityTypeBuilder<VehicleTrackingState> builder)
    {
        builder.ToTable(nameof(VehicleTrackingState), table =>
            table.HasCheckConstraint(
                "CK_VehicleTrackingState_Position",
                "([LastLatitude] IS NULL AND [LastLongitude] IS NULL) OR ([LastLatitude] BETWEEN -90 AND 90 AND [LastLongitude] BETWEEN -180 AND 180)"));

        builder.HasKey(state => state.VehicleId).HasName(PrimaryKey);
        builder.Property(state => state.VehicleId).ValueGeneratedNever();
        builder.Property(state => state.LastLatitude).HasPrecision(9, 6);
        builder.Property(state => state.LastLongitude).HasPrecision(9, 6);
        builder.Property(state => state.LastSpeedKmh).HasPrecision(6, 1);
        builder.Property(state => state.RowVersion).IsRowVersion();

        builder.HasOne<Vehicle>().WithOne().HasForeignKey<VehicleTrackingState>(state => state.VehicleId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class VehicleTrackingAlertConfiguration : IEntityTypeConfiguration<VehicleTrackingAlert>
{
    /// <summary>One open alert per vehicle and recipient.</summary>
    public const string OpenIndex = "UX_VehicleTrackingAlert_Open";

    /// <summary>The foreign key to <c>access.User</c>. Added by hand in the migration: Access owns that table.</summary>
    public const string RecipientForeignKey = "FK_VehicleTrackingAlert_User_RecipientUserId";

    public void Configure(EntityTypeBuilder<VehicleTrackingAlert> builder)
    {
        builder.ToTable(nameof(VehicleTrackingAlert), table =>
            table.HasCheckConstraint("CK_VehicleTrackingAlert_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<VehicleTrackingAlertStatus>())})"));

        builder.Property(alert => alert.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(alert => alert.RowVersion).IsRowVersion();

        builder.HasIndex(alert => new { alert.VehicleId, alert.RecipientUserId }, OpenIndex).IsUnique().HasFilter("[Status] = 'Open'");
        builder.HasOne<Vehicle>().WithMany().HasForeignKey(alert => alert.VehicleId).OnDelete(DeleteBehavior.NoAction);
    }
}
