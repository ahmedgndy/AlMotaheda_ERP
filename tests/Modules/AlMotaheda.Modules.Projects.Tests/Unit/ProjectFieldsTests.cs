using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Projects;

namespace AlMotaheda.Modules.Projects.Tests.Unit;

/// <summary>The project form's rules, enforced by the server whatever the frontend checks.</summary>
public sealed class ProjectFieldsTests
{
    [Fact]
    public void A_valid_project_is_cleaned_and_defaults_are_applied()
    {
        var errors = new ValidationErrors();

        var fields = ProjectFields.Validate(
            Valid() with { Code = "  bdr-mall-011 ", NameAr = "  ", SiteRadiusMeters = null, Status = null, Notes = "  " },
            ProjectStatus.Planning,
            errors);

        Assert.False(errors.HasErrors);
        Assert.NotNull(fields);
        Assert.Equal("BDR-MALL-011", fields.Code);
        Assert.Equal("Badr Commercial Mall", fields.NameAr);
        Assert.Equal(Project.DefaultSiteRadiusMeters, fields.SiteRadiusMeters);
        Assert.Equal(ProjectStatus.Planning, fields.Status);
        Assert.Null(fields.Notes);
        Assert.Equal(new DateOnly(2025, 3, 1), fields.StartDate);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("BDR_MALL")]
    [InlineData("BDR--MALL")]
    [InlineData("-BDR")]
    [InlineData("BDR-")]
    [InlineData("مشروع-1")]
    [InlineData("THIS-CODE-IS-MUCH-TOO-LONG-12345")]
    public void Codes_must_be_letters_and_digits_joined_by_single_hyphens(string code)
    {
        var errors = new ValidationErrors();

        Assert.Null(ProjectFields.Validate(Valid() with { Code = code }, ProjectStatus.Planning, errors));
        Assert.True(errors.Has("code"));
    }

    [Theory]
    [InlineData(null, 31.7)]
    [InlineData(30.1, null)]
    public void Site_needs_both_coordinates_or_neither(double? latitude, double? longitude)
    {
        var errors = new ValidationErrors();

        ProjectFields.Validate(
            Valid() with { SiteLatitude = (decimal?)latitude, SiteLongitude = (decimal?)longitude },
            ProjectStatus.Planning,
            errors);

        Assert.True(errors.Has(latitude is null ? "siteLatitude" : "siteLongitude"));
    }

    [Theory]
    [InlineData(91, 31)]
    [InlineData(30, -181)]
    public void Coordinates_must_be_on_earth(int latitude, int longitude)
    {
        var errors = new ValidationErrors();

        ProjectFields.Validate(Valid() with { SiteLatitude = latitude, SiteLongitude = longitude }, ProjectStatus.Planning, errors);

        Assert.True(errors.HasErrors);
    }

    [Fact]
    public void Coordinates_are_rounded_to_the_column_precision()
    {
        var fields = ProjectFields.Validate(
            Valid() with { SiteLatitude = 30.13951234m, SiteLongitude = 31.70854999m },
            ProjectStatus.Planning,
            new ValidationErrors());

        Assert.Equal(30.139512m, fields!.SiteLatitude);
        Assert.Equal(31.70855m, fields.SiteLongitude);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(5001)]
    public void Site_radius_is_50_to_5000_meters_even_without_coordinates(int radius)
    {
        var errors = new ValidationErrors();

        ProjectFields.Validate(
            Valid() with { SiteLatitude = null, SiteLongitude = null, SiteRadiusMeters = radius },
            ProjectStatus.Planning,
            errors);

        Assert.True(errors.Has("siteRadiusMeters"));
    }

    [Theory]
    [InlineData("2026-12-31", "2026-12-31")]
    [InlineData("2026-12-31", "2026-01-01")]
    public void The_end_date_must_be_after_the_start_date(string start, string end)
    {
        var errors = new ValidationErrors();

        ProjectFields.Validate(Valid() with { StartDate = start, EndDate = end }, ProjectStatus.Planning, errors);

        Assert.True(errors.Has("endDate"));
    }

    [Theory]
    [InlineData("31/12/2026")]
    [InlineData("2026-13-01")]
    [InlineData("tomorrow")]
    public void Dates_must_be_iso_dates(string value)
    {
        var errors = new ValidationErrors();

        ProjectFields.Validate(Valid() with { EndDate = value }, ProjectStatus.Planning, errors);

        Assert.True(errors.Has("endDate"));
    }

    [Theory]
    [InlineData("onHold", nameof(ProjectStatus.OnHold))]
    [InlineData("OnHold", nameof(ProjectStatus.OnHold))]
    [InlineData("completed", nameof(ProjectStatus.Completed))]
    public void Status_is_read_by_name_ignoring_case(string value, string expected)
    {
        var fields = ProjectFields.Validate(Valid() with { Status = value }, null, new ValidationErrors());

        Assert.Equal(expected, fields!.Status.ToString());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("Archived")]
    public void Unknown_or_numeric_statuses_are_refused(string value)
    {
        var errors = new ValidationErrors();

        ProjectFields.Validate(Valid() with { Status = value }, ProjectStatus.Planning, errors);

        Assert.True(errors.Has("status"));
    }

    [Fact]
    public void Status_is_required_when_editing()
    {
        var errors = new ValidationErrors();

        ProjectFields.Validate(Valid() with { Status = null }, defaultStatus: null, errors);

        Assert.True(errors.Has("status"));
    }

    [Fact]
    public void Required_fields_are_reported_together()
    {
        var errors = new ValidationErrors();

        ProjectFields.Validate(new FieldsRequest(), ProjectStatus.Planning, errors);

        var fields = errors.ToDictionary().Keys;
        Assert.Contains("code", fields);
        Assert.Contains("nameEn", fields);
        Assert.Contains("clientNameEn", fields);
        Assert.Contains("startDate", fields);
        Assert.Contains("endDate", fields);
    }

    [Fact]
    public void Changed_fields_name_what_changed_never_the_values()
    {
        var project = new Project { Code = "BDR-MALL-011", NameAr = "Badr Commercial Mall", NameEn = "Badr Commercial Mall", ClientNameEn = "Badr Urban Development Co." };
        ProjectFields.Validate(Valid(), ProjectStatus.Planning, new ValidationErrors())!.ApplyTo(project);

        var edited = ProjectFields.Validate(
            Valid() with { SiteRadiusMeters = 800, Status = "OnHold", Notes = "secret note" },
            ProjectStatus.Planning,
            new ValidationErrors())!;

        Assert.Equal("site,status,notes", edited.ChangedFields(project));
    }

    internal static FieldsRequest Valid() => new()
    {
        Code = "BDR-MALL-011",
        NameEn = "Badr Commercial Mall",
        NameAr = "المول التجاري — بدر",
        ClientNameEn = "Badr Urban Development Co.",
        LocationEn = "Badr City, Cairo",
        SiteLatitude = 30.1395m,
        SiteLongitude = 31.7085m,
        SiteRadiusMeters = 500,
        StartDate = "2025-03-01",
        EndDate = "2026-12-31",
        Status = "Active",
    };

    internal sealed record FieldsRequest : IProjectFieldsRequest
    {
        public string? Code { get; init; }
        public string? NameAr { get; init; }
        public string? NameEn { get; init; }
        public string? ClientNameAr { get; init; }
        public string? ClientNameEn { get; init; }
        public string? LocationAr { get; init; }
        public string? LocationEn { get; init; }
        public string? ContractNo { get; init; }
        public string? AddressAr { get; init; }
        public string? AddressEn { get; init; }
        public decimal? SiteLatitude { get; init; }
        public decimal? SiteLongitude { get; init; }
        public int? SiteRadiusMeters { get; init; }
        public string? StartDate { get; init; }
        public string? EndDate { get; init; }
        public string? Status { get; init; }
        public string? Notes { get; init; }
    }
}
