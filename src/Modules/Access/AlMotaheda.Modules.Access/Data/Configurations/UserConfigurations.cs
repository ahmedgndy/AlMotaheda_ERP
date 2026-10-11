using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Access.Data.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    /// <summary>Unique index names. Insert and update failures are mapped to API errors by these names.</summary>
    public const string EmailIndex = "UX_User_NormalizedEmail";
    public const string PhoneIndex = "UX_User_Phone";

    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable(nameof(User), table =>
        {
            table.HasCheckConstraint("CK_User_Status", "[Status] IN ('Active', 'Disabled')");
            table.HasCheckConstraint("CK_User_Phone", "[Phone] NOT LIKE '%[^0-9+]%'");
        });

        builder.Property(user => user.Email).HasMaxLength(256).IsUnicode();
        builder.Property(user => user.NormalizedEmail).HasMaxLength(256).IsUnicode();
        builder.HasIndex(user => user.NormalizedEmail).IsUnique().HasDatabaseName(EmailIndex);

        builder.Property(user => user.Phone).HasMaxLength(20);
        builder.HasIndex(user => user.Phone).IsUnique().HasFilter("[Phone] IS NOT NULL").HasDatabaseName(PhoneIndex);

        builder.Property(user => user.NameAr).HasMaxLength(150).IsUnicode();
        builder.Property(user => user.NameEn).HasMaxLength(150).IsUnicode();
        builder.Property(user => user.JobTitleAr).HasMaxLength(100).IsUnicode();
        builder.Property(user => user.JobTitleEn).HasMaxLength(100).IsUnicode();

        builder.HasOne(user => user.Role).WithMany().HasForeignKey(user => user.RoleId).OnDelete(DeleteBehavior.NoAction);

        builder.Property(user => user.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false).HasDefaultValue(UserStatus.Active);
        builder.Property(user => user.PasswordHash).HasMaxLength(256);

        // No database default for MustChangePassword: EF would then skip sending "false" on insert.
        // The application always sets it.

        builder.Property(user => user.RowVersion).IsRowVersion();

        builder.HasOne(user => user.SignInState).WithOne().HasForeignKey<UserSignInState>(state => state.UserId).OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany().HasForeignKey(user => user.CreatedBy).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(user => user.UpdatedBy).OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(user => user.PermissionOverrides).WithOne().HasForeignKey(link => link.UserId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class UserSignInStateConfiguration : IEntityTypeConfiguration<UserSignInState>
{
    public void Configure(EntityTypeBuilder<UserSignInState> builder)
    {
        builder.ToTable(nameof(UserSignInState), table =>
            table.HasCheckConstraint("CK_UserSignInState_FailedLoginCount", "[FailedLoginCount] >= 0"));

        builder.HasKey(state => state.UserId);
        builder.Property(state => state.UserId).ValueGeneratedNever();
        builder.Property(state => state.FailedLoginCount).HasDefaultValue((short)0);
    }
}

internal sealed class UserPermissionOverrideConfiguration : IEntityTypeConfiguration<UserPermissionOverride>
{
    public void Configure(EntityTypeBuilder<UserPermissionOverride> builder)
    {
        builder.ToTable(nameof(UserPermissionOverride));

        builder.HasKey(link => new { link.UserId, link.PermissionId });
        builder.HasOne(link => link.Permission).WithMany().HasForeignKey(link => link.PermissionId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(link => link.CreatedBy).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        var reasons = PermissionConfiguration.SqlList(Enum.GetNames<RefreshTokenRevocation>());

        builder.ToTable(nameof(RefreshToken), table =>
        {
            table.HasCheckConstraint("CK_RefreshToken_RevokedReason", $"[RevokedReason] IN ({reasons})");
            table.HasCheckConstraint(
                "CK_RefreshToken_Revocation",
                "([RevokedAt] IS NULL AND [RevokedReason] IS NULL) OR ([RevokedAt] IS NOT NULL AND [RevokedReason] IS NOT NULL)");
        });

        builder.Property(token => token.TokenHash).HasMaxLength(32).IsFixedLength();
        builder.HasIndex(token => token.TokenHash).IsUnique().HasDatabaseName("UX_RefreshToken_TokenHash");

        builder.HasIndex(token => token.FamilyId).HasDatabaseName("IX_RefreshToken_FamilyId");

        // For the future clean-up job that deletes expired tokens.
        builder.HasIndex(token => token.ExpiresAt).HasDatabaseName("IX_RefreshToken_ExpiresAt");

        // Revoking all of a user's sessions only touches tokens that are still active.
        builder.HasOne(token => token.User).WithMany().HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(token => token.UserId).HasFilter("[RevokedAt] IS NULL").HasDatabaseName("IX_RefreshToken_UserId_Active");

        builder.Property(token => token.RevokedReason).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
    }
}
