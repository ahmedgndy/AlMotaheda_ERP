using AlMotaheda.BuildingBlocks.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Access.Data.Configurations;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable(nameof(Role));

        builder.Property(role => role.Code).HasMaxLength(30);
        builder.HasIndex(role => role.Code).IsUnique().HasDatabaseName("UX_Role_Code");

        builder.Property(role => role.RowVersion).IsRowVersion();

        builder.HasMany(role => role.Permissions).WithOne().HasForeignKey(link => link.RoleId).OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany().HasForeignKey(role => role.CreatedBy).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(role => role.UpdatedBy).OnDelete(DeleteBehavior.NoAction);

        builder.HasData(PermissionCatalog.SeedRoles());
    }
}

internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable(nameof(Permission), table => table.HasCheckConstraint(
            "CK_Permission_Action",
            $"[Action] IN ({SqlList(PermissionActions.All.Select(action => action.ToString()))})"));

        // Ids are fixed by PermissionCatalog, not generated.
        builder.Property(permission => permission.Id).ValueGeneratedNever();
        builder.Property(permission => permission.Module).HasMaxLength(30);
        builder.Property(permission => permission.Action).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.HasIndex(permission => new { permission.Module, permission.Action })
            .IsUnique()
            .HasDatabaseName("UX_Permission_Module_Action");

        builder.HasOne<User>().WithMany().HasForeignKey(permission => permission.CreatedBy).OnDelete(DeleteBehavior.NoAction);

        builder.HasData(PermissionCatalog.SeedPermissions());
    }

    internal static string SqlList(IEnumerable<string> values) => string.Join(", ", values.Select(value => $"'{value}'"));
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable(nameof(RolePermission));

        builder.HasKey(link => new { link.RoleId, link.PermissionId });
        builder.HasOne(link => link.Permission).WithMany().HasForeignKey(link => link.PermissionId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(link => link.CreatedBy).OnDelete(DeleteBehavior.NoAction);

        builder.HasData(PermissionCatalog.SeedRolePermissions());
    }
}
