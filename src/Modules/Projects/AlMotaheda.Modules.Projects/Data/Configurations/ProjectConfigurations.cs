using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Projects.Data.Configurations;

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    /// <summary>Unique index on the code. A lost race on it is answered with 409 <c>project_code_taken</c>.</summary>
    public const string CodeIndex = "UX_Project_Code";

    public const int CodeMaxLength = 30;
    public const int NameMaxLength = 200;
    public const int ClientNameMaxLength = 200;
    public const int LocationMaxLength = 200;
    public const int ContractNoMaxLength = 50;
    public const int AddressMaxLength = 400;
    public const int NotesMaxLength = 2000;
    public const int MinSiteRadiusMeters = 50;
    public const int MaxSiteRadiusMeters = 5000;

    public void Configure(EntityTypeBuilder<Project> builder)
    {
        var statuses = SqlList.Of(Enum.GetNames<ProjectStatus>());

        builder.ToTable(nameof(Project), table =>
        {
            table.HasCheckConstraint("CK_Project_Status", $"[Status] IN ({statuses})");

            // Binary collation so the range means upper-case A to Z only, whatever the database collation is.
            table.HasCheckConstraint(
                "CK_Project_Code",
                "LEN([Code]) >= 2 AND [Code] COLLATE Latin1_General_BIN NOT LIKE '%[^A-Z0-9-]%'");

            table.HasCheckConstraint("CK_Project_Dates", "[EndDate] > [StartDate]");

            // Written so that one coordinate without the other fails: a CHECK passes when its result is unknown.
            table.HasCheckConstraint(
                "CK_Project_Site",
                "([SiteLatitude] IS NULL AND [SiteLongitude] IS NULL) OR ([SiteLatitude] IS NOT NULL AND [SiteLongitude] IS NOT NULL "
                + "AND [SiteLatitude] BETWEEN -90 AND 90 AND [SiteLongitude] BETWEEN -180 AND 180)");

            table.HasCheckConstraint(
                "CK_Project_SiteRadius",
                $"[SiteRadiusMeters] BETWEEN {MinSiteRadiusMeters} AND {MaxSiteRadiusMeters}");
        });

        builder.Property(project => project.Code).HasMaxLength(CodeMaxLength);
        builder.HasIndex(project => project.Code).IsUnique().HasDatabaseName(CodeIndex);

        builder.Property(project => project.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(project => project.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(project => project.ClientNameAr).HasMaxLength(ClientNameMaxLength).IsUnicode();
        builder.Property(project => project.ClientNameEn).HasMaxLength(ClientNameMaxLength).IsUnicode();
        builder.Property(project => project.LocationAr).HasMaxLength(LocationMaxLength).IsUnicode();
        builder.Property(project => project.LocationEn).HasMaxLength(LocationMaxLength).IsUnicode();
        builder.Property(project => project.ContractNo).HasMaxLength(ContractNoMaxLength).IsUnicode();
        builder.Property(project => project.AddressAr).HasMaxLength(AddressMaxLength).IsUnicode();
        builder.Property(project => project.AddressEn).HasMaxLength(AddressMaxLength).IsUnicode();
        builder.Property(project => project.Notes).HasMaxLength(NotesMaxLength).IsUnicode();

        builder.Property(project => project.SiteLatitude).HasPrecision(9, 6);
        builder.Property(project => project.SiteLongitude).HasPrecision(9, 6);

        builder.Property(project => project.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.HasIndex(project => project.Status).HasDatabaseName("IX_Project_Status");

        builder.Property(project => project.RowVersion).IsRowVersion();

        builder.HasMany(project => project.Members)
            .WithOne(member => member.Project)
            .HasForeignKey(member => member.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ProjectMemberConfiguration : IEntityTypeConfiguration<ProjectMember>
{
    /// <summary>One active membership per person per project.</summary>
    public const string ActiveUserIndex = "UX_ProjectMember_ActiveUser";

    /// <summary>One active project manager per project.</summary>
    public const string ActiveManagerIndex = "UX_ProjectMember_ActiveManager";

    /// <summary>The foreign key to <c>access.User</c>. Added by hand in the migration: Access owns that table.</summary>
    public const string UserForeignKey = "FK_ProjectMember_User_UserId";

    public const int EndReasonMaxLength = 300;

    public void Configure(EntityTypeBuilder<ProjectMember> builder)
    {
        var responsibilities = SqlList.Of(Enum.GetNames<MemberResponsibility>());

        builder.ToTable(nameof(ProjectMember), table =>
        {
            table.HasCheckConstraint("CK_ProjectMember_Responsibility", $"[Responsibility] IN ({responsibilities})");
            table.HasCheckConstraint("CK_ProjectMember_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
            table.HasCheckConstraint(
                "CK_ProjectMember_Ending",
                "([EndDate] IS NULL AND [EndedAt] IS NULL AND [EndedBy] IS NULL AND [EndReason] IS NULL) "
                + "OR ([EndDate] IS NOT NULL AND [EndedAt] IS NOT NULL AND [EndedBy] IS NOT NULL)");
        });

        builder.Property(member => member.Responsibility).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(member => member.EndReason).HasMaxLength(EndReasonMaxLength).IsUnicode();
        builder.Property(member => member.RowVersion).IsRowVersion();

        builder.HasIndex(member => new { member.ProjectId, member.UserId }, ActiveUserIndex)
            .IsUnique()
            .HasFilter("[EndDate] IS NULL");

        builder.HasIndex(member => member.ProjectId, ActiveManagerIndex)
            .IsUnique()
            .HasFilter($"[Responsibility] = '{nameof(MemberResponsibility.ProjectManager)}' AND [EndDate] IS NULL");

        // The whole team of a project, including past members.
        builder.HasIndex(member => member.ProjectId, "IX_ProjectMember_ProjectId");

        // "Which projects is this user on?" runs on every request from a user who doesn't see every project.
        builder.HasIndex(member => member.UserId, "IX_ProjectMember_UserId_Active")
            .HasFilter("[EndDate] IS NULL")
            .IncludeProperties(member => new { member.ProjectId, member.Responsibility });
    }
}
