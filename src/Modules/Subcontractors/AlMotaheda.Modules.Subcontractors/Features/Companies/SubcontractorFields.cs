using System.Text.RegularExpressions;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Companies;

/// <summary>The company form, as sent on create and on edit.</summary>
internal interface ISubcontractorFieldsRequest
{
    string? NameAr { get; }
    string? NameEn { get; }
    string? Specialty { get; }
    string? ContactName { get; }
    string? Phone { get; }
    string? Email { get; }
    string? Address { get; }
    string? Notes { get; }
    string? Status { get; }
}

/// <summary>The company form after validation.</summary>
internal sealed record SubcontractorFields(
    string NameAr,
    string NameEn,
    SubcontractorSpecialty Specialty,
    string? ContactName,
    string? Phone,
    string? Email,
    string? Address,
    string? Notes,
    SubcontractorStatus Status)
{
    /// <summary>A phone number: digits, spaces, and + - ( ). Stored as ASCII.</summary>
    private static readonly Regex PhonePattern = new(@"^\+?[0-9 ()\-]{3,30}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Checks every field and reports every problem at once. <paramref name="defaultStatus"/> is used when none is sent.</summary>
    public static SubcontractorFields? Validate(ISubcontractorFieldsRequest request, SubcontractorStatus? defaultStatus, ValidationErrors errors)
    {
        var nameAr = Input.RequiredText(request.NameAr, SubcontractorConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var nameEn = Input.RequiredText(request.NameEn, SubcontractorConfiguration.NameMaxLength, errors, "nameEn", "The English name");

        if (!Input.TryParseName<SubcontractorSpecialty>(request.Specialty, out var specialty))
        {
            errors.Add("specialty", $"specialty must be one of: {string.Join(", ", Enum.GetNames<SubcontractorSpecialty>())}.");
        }

        var contact = Input.OptionalText(request.ContactName, SubcontractorConfiguration.ContactMaxLength, errors, "contactName", "The contact name");

        var phone = Input.OptionalText(request.Phone, SubcontractorConfiguration.PhoneMaxLength, errors, "phone", "The phone");
        if (phone is not null && !errors.Has("phone") && !PhonePattern.IsMatch(phone))
        {
            errors.Add("phone", "The phone may hold only digits, spaces, and + - ( ).");
        }

        var email = Input.OptionalText(request.Email, SubcontractorConfiguration.EmailMaxLength, errors, "email", "The email");
        if (email is not null && !errors.Has("email") && (!EmailPattern.IsMatch(email) || !email.All(char.IsAscii)))
        {
            errors.Add("email", "The email is not valid.");
        }

        var address = Input.OptionalText(request.Address, SubcontractorConfiguration.AddressMaxLength, errors, "address", "The address");
        var notes = Input.OptionalText(request.Notes, SubcontractorConfiguration.NotesMaxLength, errors, "notes", "Notes");

        SubcontractorStatus status;
        if (Input.Clean(request.Status) is null && defaultStatus is { } fallback)
        {
            status = fallback;
        }
        else if (!Input.TryParseName(request.Status, out status))
        {
            errors.Add("status", "status must be Active or Inactive.");
        }

        return errors.HasErrors
            ? null
            : new SubcontractorFields(nameAr!, nameEn!, specialty, contact, phone, email, address, notes, status);
    }

    public void ApplyTo(Subcontractor company)
    {
        company.NameAr = NameAr;
        company.NameEn = NameEn;
        company.Specialty = Specialty;
        company.ContactName = ContactName;
        company.Phone = Phone;
        company.Email = Email;
        company.Address = Address;
        company.Notes = Notes;
        company.Status = Status;
    }
}

/// <summary>One company, as every company endpoint returns it.</summary>
internal sealed record SubcontractorView(
    int Id,
    string NameAr,
    string NameEn,
    string Specialty,
    string? ContactName,
    string? Phone,
    string? Email,
    string? Address,
    string? Notes,
    string Status,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion)
{
    public static SubcontractorView From(Subcontractor company) => new(
        company.Id,
        company.NameAr,
        company.NameEn,
        company.Specialty.ToString(),
        company.ContactName,
        company.Phone,
        company.Email,
        company.Address,
        company.Notes,
        company.Status.ToString(),
        company.CreatedAt,
        company.CreatedBy,
        company.UpdatedAt,
        company.UpdatedBy,
        RowVersions.Format(company.RowVersion));
}

/// <summary>The company list query, as <see cref="IQueryable{T}"/> so a test can prove it translates.</summary>
internal static class SubcontractorQueries
{
    /// <summary>Search in the names, the contact and the phone; filter by specialty and status; order by English name.</summary>
    public static IQueryable<Subcontractor> Search(
        SubcontractorsDbContext db, string? search, SubcontractorSpecialty? specialty, SubcontractorStatus? status)
    {
        var query = db.Subcontractors.AsNoTracking();

        if (search is { } text)
        {
            query = query.Where(company =>
                company.NameEn.Contains(text)
                || company.NameAr.Contains(text)
                || (company.ContactName != null && company.ContactName.Contains(text))
                || (company.Phone != null && company.Phone.Contains(text)));
        }

        if (specialty is { } wantedSpecialty)
        {
            query = query.Where(company => company.Specialty == wantedSpecialty);
        }

        if (status is { } wantedStatus)
        {
            query = query.Where(company => company.Status == wantedStatus);
        }

        return query.OrderBy(company => company.NameEn).ThenBy(company => company.Id);
    }
}
