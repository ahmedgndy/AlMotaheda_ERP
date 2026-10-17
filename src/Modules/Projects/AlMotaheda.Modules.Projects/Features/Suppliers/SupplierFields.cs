using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Suppliers;

/// <summary>The supplier form, as sent on create and on edit.</summary>
internal interface ISupplierFieldsRequest
{
    string? NameAr { get; }
    string? NameEn { get; }
    string? Kind { get; }
    string? Phone { get; }
    string? TaxNumber { get; }
    string? Notes { get; }
    string? Status { get; }

    // Optional profile fields (docs/cost-flow §2.5). Left out (null) keeps the saved value; "" clears it, so an app
    // that doesn't send them yet never wipes them.
    string? LegalName { get; }
    string? ContactPerson { get; }
    string? Email { get; }
    string? Address { get; }
    string? CommercialRegistrationNo { get; }
}

/// <summary>The optional profile fields after validation. <c>null</c> = keep; "" = clear.</summary>
internal sealed record SupplierProfileFields(string? LegalName, string? ContactPerson, string? Email, string? Address, string? CommercialRegistrationNo)
{
    public static SupplierProfileFields Validate(ISupplierFieldsRequest request, ValidationErrors errors)
    {
        string? Field(string? value, int max, string field, string label) =>
            value is null ? null : Input.OptionalText(value, max, errors, field, label) ?? "";

        var email = Field(request.Email, SupplierConfiguration.EmailMaxLength, "email", "The email");
        if (email is { Length: > 0 } && (!email.Contains('@') || email.StartsWith('@') || email.EndsWith('@') || email.Contains(' ')))
        {
            errors.Add("email", "The email is not valid.");
        }

        return new SupplierProfileFields(
            Field(request.LegalName, SupplierConfiguration.NameMaxLength, "legalName", "The legal name"),
            Field(request.ContactPerson, SupplierConfiguration.NameMaxLength, "contactPerson", "The contact person"),
            email,
            Field(request.Address, SupplierConfiguration.AddressMaxLength, "address", "The address"),
            Field(request.CommercialRegistrationNo, SupplierConfiguration.TaxNumberMaxLength, "commercialRegistrationNo", "The commercial registration number"));
    }

    public void ApplyTo(Supplier supplier)
    {
        static string? Keep(string? value, string? current) => value is null ? current : value.Length == 0 ? null : value;

        supplier.LegalName = Keep(LegalName, supplier.LegalName);
        supplier.ContactPerson = Keep(ContactPerson, supplier.ContactPerson);
        supplier.Email = Keep(Email, supplier.Email);
        supplier.Address = Keep(Address, supplier.Address);
        supplier.CommercialRegistrationNo = Keep(CommercialRegistrationNo, supplier.CommercialRegistrationNo);
    }
}

/// <summary>The supplier form after validation. One set of rules for create and edit.</summary>
internal sealed record SupplierFields(
    string NameAr,
    string NameEn,
    SupplierKind Kind,
    string? Phone,
    string? TaxNumber,
    string? Notes,
    SupplierStatus Status)
{
    /// <param name="defaultStatus">Used when the form sends no status (create); <c>null</c> makes the status required (edit).</param>
    public static SupplierFields? Validate(ISupplierFieldsRequest request, SupplierStatus? defaultStatus, ValidationErrors errors)
    {
        var nameAr = Input.RequiredText(request.NameAr, SupplierConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var nameEn = Input.RequiredText(request.NameEn, SupplierConfiguration.NameMaxLength, errors, "nameEn", "The English name");

        if (!Input.TryParseName<SupplierKind>(request.Kind, out var kind))
        {
            errors.Add("kind", $"kind must be one of: {string.Join(", ", Enum.GetNames<SupplierKind>())}.");
        }

        var phone = Input.OptionalText(request.Phone, SupplierConfiguration.PhoneMaxLength, errors, "phone", "The phone");
        var taxNumber = Input.OptionalText(request.TaxNumber, SupplierConfiguration.TaxNumberMaxLength, errors, "taxNumber", "The tax number");
        var notes = Input.OptionalText(request.Notes, SupplierConfiguration.NotesMaxLength, errors, "notes", "Notes");

        var status = defaultStatus ?? SupplierStatus.Active;
        if (Input.Clean(request.Status) is { } statusText)
        {
            if (!Input.TryParseName(statusText, out status))
            {
                errors.Add("status", "status must be Active or Inactive.");
            }
        }
        else if (defaultStatus is null)
        {
            errors.Add("status", "status is required.");
        }

        return errors.HasErrors ? null : new SupplierFields(nameAr!, nameEn!, kind, phone, taxNumber, notes, status);
    }

    public void ApplyTo(Supplier supplier)
    {
        supplier.NameAr = NameAr;
        supplier.NameEn = NameEn;
        supplier.Kind = Kind;
        supplier.Phone = Phone;
        supplier.TaxNumber = TaxNumber;
        supplier.Notes = Notes;
        supplier.Status = Status;
    }
}

/// <summary>One supplier, as the list and the forms return it.</summary>
internal sealed record SupplierView(
    int Id,
    string NameAr,
    string NameEn,
    string Kind,
    string? Phone,
    string? TaxNumber,
    string? Notes,
    string Status,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion,
    string? LegalName,
    string? ContactPerson,
    string? Email,
    string? Address,
    string? CommercialRegistrationNo)
{
    public static SupplierView From(Supplier supplier) => new(
        supplier.Id,
        supplier.NameAr,
        supplier.NameEn,
        supplier.Kind.ToString(),
        supplier.Phone,
        supplier.TaxNumber,
        supplier.Notes,
        supplier.Status.ToString(),
        supplier.CreatedAt,
        supplier.CreatedBy,
        supplier.UpdatedAt,
        supplier.UpdatedBy,
        RowVersions.Format(supplier.RowVersion),
        supplier.LegalName,
        supplier.ContactPerson,
        supplier.Email,
        supplier.Address,
        supplier.CommercialRegistrationNo);
}

/// <summary>The supplier queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class SupplierQueries
{
    /// <summary>The company's suppliers, filtered in SQL and ordered by English name.</summary>
    public static IQueryable<Supplier> Search(ProjectsDbContext db, string? search, SupplierStatus? status, SupplierKind? kind)
    {
        var query = db.Suppliers.AsNoTracking();

        if (search is not null)
        {
            // The database collation ignores case, so "cem" finds "Cemex".
            query = query.Where(supplier =>
                supplier.NameEn.Contains(search)
                || supplier.NameAr.Contains(search)
                || (supplier.Phone != null && supplier.Phone.Contains(search)));
        }

        if (status is { } wanted)
        {
            query = query.Where(supplier => supplier.Status == wanted);
        }

        if (kind is { } wantedKind)
        {
            query = query.Where(supplier => supplier.Kind == wantedKind);
        }

        return query.OrderBy(supplier => supplier.NameEn).ThenBy(supplier => supplier.Id);
    }
}
