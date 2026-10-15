using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Validation;
using AlMotaheda.Modules.Archive.Common;
using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Archive.Features.Documents;
using AlMotaheda.Modules.Archive.Security;
using AlMotaheda.Modules.Archive.Storage;

namespace AlMotaheda.Modules.Archive.Tests.Unit;

/// <summary>Input rules, who may change a document, the size limits per kind, and upload dates at the company.</summary>
public sealed class DocumentRulesTests
{
    private static readonly DateOnly Today = new(2026, 1, 15);

    [Fact]
    public void Metadata_reports_every_failing_field_at_once()
    {
        var errors = new ValidationErrors();

        var fields = DocumentFields.Validate(
            title: " ", category: "Photo", description: new string('x', 1001), documentDate: "2026-01-16", Today, errors);

        Assert.Null(fields);
        Assert.Equal(["category", "description", "documentDate", "title"], errors.ToDictionary().Keys.Order());
    }

    [Fact]
    public void Metadata_is_trimmed_and_categories_are_read_in_any_letter_case()
    {
        var errors = new ValidationErrors();

        var fields = DocumentFields.Validate("  Certificate 7  ", "paymentcertificate", "  ", "2026-01-15", Today, errors);

        Assert.False(errors.HasErrors);
        Assert.Equal(new DocumentFields("Certificate 7", DocumentCategory.PaymentCertificate, null, Today), fields);
    }

    [Theory]
    [InlineData("Expense", "12", true)]
    [InlineData(null, null, true)]
    [InlineData("Expense", null, false)]
    [InlineData(null, "12", false)]
    [InlineData("Invoice", "12", false)]
    [InlineData("Expense", "0", false)]
    [InlineData("Expense", "-3", false)]
    [InlineData("Expense", "1e3", false)]
    public void An_upload_link_needs_a_known_record_type_and_a_positive_id(string? entityType, string? entityId, bool valid)
    {
        var errors = new ValidationErrors();

        var link = LinkInput.ValidateOne(entityType, entityId, errors);

        Assert.Equal(valid, !errors.HasErrors);
        if (valid && entityType is not null)
        {
            Assert.Equal((LinkedEntityType.Expense, 12), link);
        }
    }

    [Fact]
    public void A_links_list_reports_each_bad_entry_by_position()
    {
        var errors = new ValidationErrors();

        var links = LinkInput.ValidateList(
            [new("DailyReport", 3), new("Photo", 4), new("Expense", 0), new("dailyreport", 3), null],
            errors);

        Assert.Null(links);
        Assert.Equal(
            ["links[1].entityType", "links[2].entityId", "links[3]", "links[4].entityId", "links[4].entityType"],
            errors.ToDictionary().Keys.Order());
    }

    [Fact]
    public void An_empty_links_list_removes_every_link_and_a_missing_one_is_an_error()
    {
        var errors = new ValidationErrors();

        Assert.Empty(LinkInput.ValidateList([], errors)!);
        Assert.False(errors.HasErrors);

        Assert.Null(LinkInput.ValidateList(null, errors));
        Assert.Equal(["links"], errors.ToDictionary().Keys);
    }

    [Fact]
    public void More_than_twenty_links_are_refused()
    {
        var errors = new ValidationErrors();

        LinkInput.ValidateList(Enumerable.Range(1, 21).Select(id => new DocumentLinkRequest("Expense", id)).ToList(), errors);

        Assert.Equal(["links"], errors.ToDictionary().Keys);
    }

    [Theory]
    [InlineData("VCEF", 1000, true)]   // projects Edit: any document
    [InlineData("VE", 2000, true)]
    [InlineData("VC", 1000, true)]     // the uploader, who may still upload
    [InlineData("VC", 2000, false)]    // someone else's document
    [InlineData("V", 1000, false)]     // the uploader lost the right to upload
    [InlineData("VAF", 1000, false)]
    public void Only_the_uploader_or_projects_edit_changes_a_document(string letters, int uploadedBy, bool allowed)
    {
        var caller = Caller(userId: 1000, letters);

        Assert.Equal(allowed, caller.CanChange(uploadedBy));
    }

    [Theory]
    [InlineData("C", true)]
    [InlineData("E", true)]
    [InlineData("VAF", false)]
    public void Upload_needs_projects_create_or_edit(string letters, bool allowed)
    {
        Assert.Equal(allowed, Caller(1000, letters).CanUpload);
    }

    [Fact]
    public void Each_kind_has_its_own_size_limit()
    {
        var limits = new ArchiveLimitsOptions();

        Assert.Equal(25L * 1024 * 1024, limits.MaxBytesFor(DocumentKind.Image));
        Assert.Equal(50L * 1024 * 1024, limits.MaxBytesFor(DocumentKind.Pdf));
        Assert.Equal(50L * 1024 * 1024, limits.MaxBytesFor(DocumentKind.Office));
        Assert.Equal(50L * 1024 * 1024, limits.MaxBytesFor(DocumentKind.Drawing));
        Assert.Equal(500L * 1024 * 1024, limits.MaxBytesFor(DocumentKind.Video));

        // The upload request may carry the largest file (a video) plus 1 MB of form around it.
        Assert.Equal(501L * 1024 * 1024, limits.MaxRequestBytes);
    }

    [Fact]
    public void Limits_outside_1_mb_to_10_gb_stop_the_application_at_startup()
    {
        var validator = new ArchiveLimitsOptionsValidator();

        var result = validator.Validate(null, new ArchiveLimitsOptions { ImageMaxMegabytes = 0, VideoMaxMegabytes = 20_000 });

        Assert.True(result.Failed);
        Assert.Contains("ImageMaxMegabytes", result.FailureMessage);
        Assert.Contains("VideoMaxMegabytes", result.FailureMessage);
        Assert.DoesNotContain("DocumentMaxMegabytes", result.FailureMessage);
    }

    [Fact]
    public void Upload_dates_are_company_days_turned_into_utc()
    {
        var cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

        // Winter: Cairo is UTC+2, so 15 January starts at 22:00 UTC the day before.
        Assert.Equal(new DateTime(2026, 1, 14, 22, 0, 0, DateTimeKind.Utc), ArchiveCalendar.StartOfDayUtc(new DateOnly(2026, 1, 15), cairo));

        // Summer time starts at midnight on the last Friday of April: 00:00 doesn't exist, the day starts at 01:00 (UTC+3).
        Assert.Equal(new DateTime(2026, 4, 23, 22, 0, 0, DateTimeKind.Utc), ArchiveCalendar.StartOfDayUtc(new DateOnly(2026, 4, 24), cairo));
    }

    private static ArchiveCaller Caller(int userId, string letters)
    {
        var claims = new[]
        {
            new Claim(AccessClaims.UserId, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(AccessClaims.Permission, "projects:" + letters),
        };
        return ArchiveCaller.From(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")))!;
    }
}
