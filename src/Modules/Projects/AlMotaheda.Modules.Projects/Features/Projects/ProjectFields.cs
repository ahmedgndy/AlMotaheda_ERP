using System.Text.RegularExpressions;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;

namespace AlMotaheda.Modules.Projects.Features.Projects;

/// <summary>The project fields a caller sends when creating or editing a project. Dates are <c>yyyy-MM-dd</c>.</summary>
internal interface IProjectFieldsRequest
{
    string? Code { get; }
    string? NameAr { get; }
    string? NameEn { get; }
    string? ClientNameAr { get; }
    string? ClientNameEn { get; }
    string? LocationAr { get; }
    string? LocationEn { get; }
    string? ContractNo { get; }
    string? AddressAr { get; }
    string? AddressEn { get; }
    decimal? SiteLatitude { get; }
    decimal? SiteLongitude { get; }
    int? SiteRadiusMeters { get; }
    string? StartDate { get; }
    string? EndDate { get; }
    string? Status { get; }
    string? Notes { get; }
}

/// <summary>Validated and cleaned project fields, ready to store. The rules come from the prototype's project form.</summary>
internal sealed partial record ProjectFields(
    string Code,
    string NameAr,
    string NameEn,
    string? ClientNameAr,
    string ClientNameEn,
    string? LocationAr,
    string? LocationEn,
    string? ContractNo,
    string? AddressAr,
    string? AddressEn,
    decimal? SiteLatitude,
    decimal? SiteLongitude,
    int SiteRadiusMeters,
    DateOnly StartDate,
    DateOnly EndDate,
    ProjectStatus Status,
    string? Notes)
{
    public const int CodeMinLength = 2;

    /// <summary>Validates every field and returns the cleaned values, or <c>null</c> with the errors added.</summary>
    /// <param name="defaultStatus">Used when no status is sent (Planning for a new project).</param>
    public static ProjectFields? Validate(IProjectFieldsRequest request, ProjectStatus? defaultStatus, ValidationErrors errors)
    {
        var code = ValidateCode(request.Code, errors);

        var nameEn = Input.RequiredText(request.NameEn, ProjectConfiguration.NameMaxLength, errors, "nameEn", "English name");
        // As in the prototype, the Arabic name falls back to the English one.
        var nameAr = Input.OptionalText(request.NameAr, ProjectConfiguration.NameMaxLength, errors, "nameAr", "Arabic name") ?? nameEn;

        var clientNameEn = Input.RequiredText(request.ClientNameEn, ProjectConfiguration.ClientNameMaxLength, errors, "clientNameEn", "Client name (English)");
        var clientNameAr = Input.OptionalText(request.ClientNameAr, ProjectConfiguration.ClientNameMaxLength, errors, "clientNameAr", "Client name (Arabic)");
        var locationAr = Input.OptionalText(request.LocationAr, ProjectConfiguration.LocationMaxLength, errors, "locationAr", "Location (Arabic)");
        var locationEn = Input.OptionalText(request.LocationEn, ProjectConfiguration.LocationMaxLength, errors, "locationEn", "Location (English)");
        var contractNo = Input.OptionalText(request.ContractNo, ProjectConfiguration.ContractNoMaxLength, errors, "contractNo", "Contract number");
        var addressAr = Input.OptionalText(request.AddressAr, ProjectConfiguration.AddressMaxLength, errors, "addressAr", "Site address (Arabic)");
        var addressEn = Input.OptionalText(request.AddressEn, ProjectConfiguration.AddressMaxLength, errors, "addressEn", "Site address (English)");
        var notes = Input.OptionalText(request.Notes, ProjectConfiguration.NotesMaxLength, errors, "notes", "Notes");

        var (latitude, longitude) = ValidateSite(request.SiteLatitude, request.SiteLongitude, errors);

        var radius = request.SiteRadiusMeters ?? Project.DefaultSiteRadiusMeters;
        if (radius is < ProjectConfiguration.MinSiteRadiusMeters or > ProjectConfiguration.MaxSiteRadiusMeters)
        {
            errors.Add("siteRadiusMeters",
                $"Site radius must be between {ProjectConfiguration.MinSiteRadiusMeters} and {ProjectConfiguration.MaxSiteRadiusMeters} meters.");
        }

        var startDate = Input.RequiredDate(request.StartDate, errors, "startDate", "Start date");
        var endDate = Input.RequiredDate(request.EndDate, errors, "endDate", "Expected end date");
        if (startDate is not null && endDate is not null && endDate <= startDate)
        {
            errors.Add("endDate", "The expected end date must be after the start date.");
        }

        ProjectStatus status;
        if (Input.Clean(request.Status) is null && defaultStatus is not null)
        {
            status = defaultStatus.Value;
        }
        else if (!Input.TryParseName(request.Status, out status))
        {
            errors.Add("status", $"status must be one of: {string.Join(", ", Enum.GetNames<ProjectStatus>())}.");
        }

        if (errors.HasErrors)
        {
            return null;
        }

        return new ProjectFields(
            code!, nameAr!, nameEn!, clientNameAr, clientNameEn!, locationAr, locationEn, contractNo, addressAr, addressEn,
            latitude, longitude, radius, startDate!.Value, endDate!.Value, status, notes);
    }

    public void ApplyTo(Project project)
    {
        project.Code = Code;
        project.NameAr = NameAr;
        project.NameEn = NameEn;
        project.ClientNameAr = ClientNameAr;
        project.ClientNameEn = ClientNameEn;
        project.LocationAr = LocationAr;
        project.LocationEn = LocationEn;
        project.ContractNo = ContractNo;
        project.AddressAr = AddressAr;
        project.AddressEn = AddressEn;
        project.SiteLatitude = SiteLatitude;
        project.SiteLongitude = SiteLongitude;
        project.SiteRadiusMeters = SiteRadiusMeters;
        project.StartDate = StartDate;
        project.EndDate = EndDate;
        project.Status = Status;
        project.Notes = Notes;
    }

    /// <summary>For the audit log: which fields change, by name only (never the values).</summary>
    public string ChangedFields(Project project)
    {
        var changed = new List<string>();
        if (project.Code != Code) changed.Add("code");
        if (project.NameAr != NameAr || project.NameEn != NameEn) changed.Add("name");
        if (project.ClientNameAr != ClientNameAr || project.ClientNameEn != ClientNameEn) changed.Add("client");
        if (project.LocationAr != LocationAr || project.LocationEn != LocationEn) changed.Add("location");
        if (project.ContractNo != ContractNo) changed.Add("contractNo");
        if (project.AddressAr != AddressAr || project.AddressEn != AddressEn) changed.Add("address");
        if (project.SiteLatitude != SiteLatitude || project.SiteLongitude != SiteLongitude || project.SiteRadiusMeters != SiteRadiusMeters)
        {
            changed.Add("site");
        }

        if (project.StartDate != StartDate || project.EndDate != EndDate) changed.Add("dates");
        if (project.Status != Status) changed.Add("status");
        if (project.Notes != Notes) changed.Add("notes");
        return changed.Count == 0 ? "none" : string.Join(",", changed);
    }

    private static string? ValidateCode(string? value, ValidationErrors errors)
    {
        var code = Input.Clean(value)?.ToUpperInvariant();
        if (code is null)
        {
            errors.Add("code", "Project code is required.");
            return null;
        }

        if (code.Length is < CodeMinLength or > ProjectConfiguration.CodeMaxLength || !CodePattern().IsMatch(code))
        {
            errors.Add("code",
                $"Project code must be {CodeMinLength} to {ProjectConfiguration.CodeMaxLength} letters or digits, "
                + "in groups joined by single hyphens, for example BDR-MALL-011.");
            return null;
        }

        return code;
    }

    private static (decimal? Latitude, decimal? Longitude) ValidateSite(decimal? latitude, decimal? longitude, ValidationErrors errors)
    {
        if (latitude is null && longitude is null)
        {
            return (null, null);
        }

        if (latitude is null || longitude is null)
        {
            errors.Add(latitude is null ? "siteLatitude" : "siteLongitude", "Enter both latitude and longitude, or neither.");
            return (null, null);
        }

        if (latitude is < -90 or > 90)
        {
            errors.Add("siteLatitude", "Latitude must be between -90 and 90.");
        }

        if (longitude is < -180 or > 180)
        {
            errors.Add("siteLongitude", "Longitude must be between -180 and 180.");
        }

        // Stored with six decimals (about 0.1 m), the column's precision.
        return (Math.Round(latitude.Value, 6), Math.Round(longitude.Value, 6));
    }

    [GeneratedRegex("^[A-Z0-9]+(-[A-Z0-9]+)*$")]
    private static partial Regex CodePattern();
}
