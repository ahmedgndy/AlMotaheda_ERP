using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Archive.Tests.Database;

/// <summary>
/// The archive end to end on SQL Server: upload, list, download and preview, the project scope on every endpoint
/// (file bytes included), who may change what, links, soft delete, and "a failed save leaves no file".
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class DocumentArchiveTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Upload_list_and_download_round_trip()
    {
        await using var api = await ArchiveApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (manager, managerSession) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineer, engineerSession) = await api.CreateSignedInUserAsync(executive, "engineer");
        var projectId = await api.CreateProjectAsync(executive, "ARC-001", manager, siteEngineers: [engineer]);
        var pdf = SampleFiles.Pdf(100_000);
        var photo = SampleFiles.CameraJpeg(250_000);

        var certificate = await api.UploadAsync(engineerSession, projectId, "../../certificate 7.pdf", pdf);
        var sitePhoto = await api.UploadAsync(engineerSession, projectId, "IMG_0001.JPG", photo, new()
        {
            ["title"] = "Slab casting, level 3",
            ["category"] = "sitePhoto",
            ["description"] = "North side",
            ["documentDate"] = "2026-01-10",
        });

        Assert.Equal("Pdf", certificate.GetProperty("kind").GetString());
        Assert.Equal("application/pdf", certificate.GetProperty("contentType").GetString());
        Assert.Equal("certificate 7.pdf", certificate.GetProperty("fileName").GetString());
        Assert.Equal(100_000, certificate.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(pdf)), certificate.GetProperty("sha256").GetString());
        Assert.Equal(engineer, certificate.GetProperty("uploader").GetProperty("userId").GetInt32());
        Assert.True(certificate.GetProperty("abilities").GetProperty("edit").GetBoolean());
        Assert.True(certificate.GetProperty("abilities").GetProperty("preview").GetBoolean());
        Assert.Equal("Image", sitePhoto.GetProperty("kind").GetString());
        Assert.Equal("SitePhoto", sitePhoto.GetProperty("category").GetString());
        Assert.Equal("2026-01-10", sitePhoto.GetProperty("documentDate").GetString());
        Assert.Equal(2, api.StoredFiles.Count);

        // The manager sees both, newest first, with the count of each category.
        var list = await api.ListAsync(managerSession, projectId);
        Assert.Equal(2, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            [sitePhoto.GetProperty("id").GetInt32(), certificate.GetProperty("id").GetInt32()],
            list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()));
        Assert.Equal(
            ["PaymentCertificate:1", "SitePhoto:1"],
            list.GetProperty("categoryCounts").EnumerateArray().Select(count => $"{count.GetProperty("category").GetString()}:{count.GetProperty("count").GetInt32()}"));
        Assert.True(list.GetProperty("items")[0].GetProperty("abilities").GetProperty("edit").GetBoolean());

        Assert.Equal(1, (await api.ListAsync(managerSession, projectId, "&category=SitePhoto")).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await api.ListAsync(managerSession, projectId, "&search=certificate")).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await api.ListAsync(managerSession, projectId, "&search=North")).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await api.ListAsync(managerSession, projectId, "&kind=Pdf")).GetProperty("totalCount").GetInt32());
        Assert.Equal(2, (await api.ListAsync(managerSession, projectId, $"&uploadedBy={engineer}")).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await api.ListAsync(managerSession, projectId, "&to=2020-01-01")).GetProperty("totalCount").GetInt32());

        // Download: the same bytes, never cached, never sniffed, sandboxed.
        var download = await api.GetContentAsync(managerSession, projectId, certificate.GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(pdf, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("certificate 7.pdf", download.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.True(download.Headers.CacheControl?.NoStore);
        Assert.True(download.Headers.CacheControl?.Private);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("sandbox", download.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("bytes", download.Headers.AcceptRanges.Single());

        // Preview in the browser.
        var preview = await api.GetContentAsync(managerSession, projectId, sitePhoto.GetProperty("id").GetInt32(), "?disposition=inline");
        Assert.Equal("inline", preview.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("image/jpeg", preview.Content.Headers.ContentType?.MediaType);
        Assert.Equal(photo, await preview.Content.ReadAsByteArrayAsync());

        // A range, as a video player or a PDF viewer asks for it.
        var range = await api.GetContentAsync(managerSession, projectId, sitePhoto.GetProperty("id").GetInt32(), null,
            request => request.Headers.Range = new RangeHeaderValue(1000, 1099));
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(photo[1000..1100], await range.Content.ReadAsByteArrayAsync());
        Assert.Equal(250_000, range.Content.Headers.ContentRange?.Length);

        var row = await api.QueryAsync(db => db.Documents.AsNoTracking().SingleAsync(document => document.Id == certificate.GetProperty("id").GetInt32()));
        Assert.Equal(projectId, row.ProjectId);
        Assert.StartsWith($"{projectId}/", row.StorageKey);
        Assert.Null(row.DeletedAt);
    }

    [DatabaseFact]
    public async Task A_photo_sent_again_from_the_offline_queue_is_stored_once_with_its_capture_time()
    {
        await using var api = await ArchiveApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (manager, _) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineer, engineerSession) = await api.CreateSignedInUserAsync(executive, "engineer");
        var projectId = await api.CreateProjectAsync(executive, "ARC-OFF", manager, siteEngineers: [engineer]);
        var otherProject = await api.CreateProjectAsync(executive, "ARC-OFF2", manager, siteEngineers: [engineer]);
        var reportId = await api.CreateDailyReportAsync(engineerSession, projectId);
        var photo = SampleFiles.CameraJpeg(120_000);
        var key = Guid.NewGuid().ToString();
        var capturedAt = DateTime.UtcNow.AddHours(-6);
        var fields = new Dictionary<string, string>
        {
            ["title"] = "Slab, offline photo",
            ["category"] = "SitePhoto",
            ["entityType"] = "DailyReport",
            ["entityId"] = reportId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["capturedAt"] = capturedAt.ToString("o"),
        };

        var first = await api.PostUploadAsync(engineerSession, projectId, "IMG_0042.JPG", photo, fields, key);
        await EnsureStatus(first, HttpStatusCode.Created);
        var document = await Json(first);
        Assert.Equal(capturedAt, document.GetProperty("capturedAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(5));
        Assert.Equal("DailyReport", Assert.Single(document.GetProperty("links").EnumerateArray()).GetProperty("entityType").GetString());

        // The answer was lost: the same upload again returns the first document and stores no second file.
        var again = await api.PostUploadAsync(engineerSession, projectId, "IMG_0042.JPG", photo, fields, key);
        await EnsureStatus(again, HttpStatusCode.OK);
        Assert.Equal(document.GetProperty("id").GetInt32(), (await Json(again)).GetProperty("id").GetInt32());
        Assert.Single(api.StoredFiles);
        Assert.Equal(1, await api.QueryAsync(db => db.Documents.CountAsync()));

        // The same key on another project is refused; a key that is not a GUID too.
        var elsewhere = await api.PostUploadAsync(engineerSession, otherProject, "IMG_0042.JPG", photo, new() { ["title"] = "x", ["category"] = "SitePhoto" }, key);
        await EnsureStatus(elsewhere, HttpStatusCode.Conflict);
        Assert.Equal("idempotency_key_reused", await CodeOf(elsewhere));
        var badKey = await api.PostUploadAsync(engineerSession, projectId, "IMG_0043.JPG", photo, new() { ["title"] = "x", ["category"] = "SitePhoto" }, "not-a-guid");
        await EnsureStatus(badKey, HttpStatusCode.BadRequest);
        var badTime = await api.PostUploadAsync(engineerSession, projectId, "IMG_0044.JPG", photo, new() { ["title"] = "x", ["category"] = "SitePhoto", ["capturedAt"] = DateTime.UtcNow.AddDays(1).ToString("o") });
        await EnsureStatus(badTime, HttpStatusCode.BadRequest);
        Assert.Single(api.StoredFiles);
    }

    [DatabaseFact]
    public async Task Office_files_and_drawings_always_download_even_when_a_preview_is_asked()
    {
        await using var api = await ArchiveApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (manager, managerSession) = await api.CreateSignedInUserAsync(executive, "manager");
        var projectId = await api.CreateProjectAsync(executive, "ARC-002", manager);

        var boq = await api.UploadAsync(managerSession, projectId, "boq.xlsx", SampleFiles.Xlsx(), new() { ["title"] = "BOQ", ["category"] = "Contract" });
        var plan = await api.UploadAsync(managerSession, projectId, "plan", SampleFiles.Dwg(), new() { ["title"] = "Ground floor", ["category"] = "Drawing" });

        Assert.Equal("Office", boq.GetProperty("kind").GetString());
        Assert.False(boq.GetProperty("abilities").GetProperty("preview").GetBoolean());
        Assert.Equal("plan.dwg", plan.GetProperty("fileName").GetString());

        foreach (var document in new[] { boq, plan })
        {
            var response = await api.GetContentAsync(managerSession, projectId, document.GetProperty("id").GetInt32(), "?disposition=inline");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        }
    }

    [DatabaseFact]
    public async Task A_failed_save_leaves_no_file()
    {
        // The scope check says yes for a project that doesn't exist, so the insert fails on the foreign key
        // to projects.Project after the file was stored.
        await using var api = await ArchiveApi.StartAsync(database, builder => builder.Services.AddScoped<IProjectAccess, AnyProjectIsVisible>());
        var executive = await api.SignInExecutiveAsync();

        var response = await api.PostUploadAsync(executive, 987_654, "certificate.pdf", SampleFiles.Pdf(50_000));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(api.StoredFiles);
        Assert.Equal(0, await api.QueryAsync(db => db.Documents.CountAsync()));
    }

    [DatabaseFact]
    public async Task A_project_the_caller_cannot_see_answers_404_on_list_metadata_and_content()
    {
        await using var api = await ArchiveApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (managerA, managerASession) = await api.CreateSignedInUserAsync(executive, "manager");
        var (managerB, managerBSession) = await api.CreateSignedInUserAsync(executive, "manager");
        var projectA = await api.CreateProjectAsync(executive, "ARC-A01", managerA);
        await api.CreateProjectAsync(executive, "ARC-B01", managerB);
        var document = await api.UploadAsync(managerASession, projectA, "certificate.pdf", SampleFiles.Pdf());
        var id = document.GetProperty("id").GetInt32();

        var list = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectA}/documents", managerBSession);
        var metadata = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectA}/documents/{id}", managerBSession);
        var content = await api.GetContentAsync(managerBSession, projectA, id);
        var inline = await api.GetContentAsync(managerBSession, projectA, id, "?disposition=inline");
        var upload = await api.PostUploadAsync(managerBSession, projectA, "other.pdf", SampleFiles.Pdf());
        var delete = await api.SendAsync(HttpMethod.Delete, $"/api/projects/{projectA}/documents/{id}", managerBSession);

        foreach (var response in new[] { list, metadata, content, inline, upload, delete })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("not_found", await CodeOf(response));
        }

        Assert.Single(api.StoredFiles);
    }

    [DatabaseFact]
    public async Task A_document_id_from_another_project_is_not_found_through_a_visible_project()
    {
        await using var api = await ArchiveApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (managerA, managerASession) = await api.CreateSignedInUserAsync(executive, "manager");
        var (managerB, managerBSession) = await api.CreateSignedInUserAsync(executive, "manager");
        var projectA = await api.CreateProjectAsync(executive, "ARC-A02", managerA);
        var projectB = await api.CreateProjectAsync(executive, "ARC-B02", managerB);
        var documentOfA = (await api.UploadAsync(managerASession, projectA, "certificate.pdf", SampleFiles.Pdf())).GetProperty("id").GetInt32();

        // Manager B asks for A's document through B's own project.
        var metadata = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectB}/documents/{documentOfA}", managerBSession);
        var content = await api.GetContentAsync(managerBSession, projectB, documentOfA);

        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
    }

    [DatabaseFact]
    public async Task Only_the_uploader_or_projects_edit_changes_a_document()
    {
        await using var api = await ArchiveApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (manager, managerSession) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineer, engineerSession) = await api.CreateSignedInUserAsync(executive, "engineer");
        var (otherEngineer, otherEngineerSession) = await api.CreateSignedInUserAsync(executive, "engineer");
        var (staff, staffSession) = await api.CreateSignedInUserAsync(executive, "staff");
        var projectId = await api.CreateProjectAsync(executive, "ARC-003", manager, siteEngineers: [engineer, otherEngineer], team: [staff]);
        var document = await api.UploadAsync(engineerSession, projectId, "minutes.pdf", SampleFiles.Pdf(), new() { ["title"] = "Minutes 1", ["category"] = "MeetingMinutes" });
        var path = $"/api/projects/{projectId}/documents/{document.GetProperty("id").GetInt32()}";
        object Edit(JsonElement current, string title) => new
        {
            title,
            category = "MeetingMinutes",
            description = "Weekly site meeting",
            documentDate = "2026-01-12",
            rowVersion = current.GetProperty("rowVersion").GetString(),
        };

        // Staff (projects View) reads but can't upload; another engineer can't change someone else's document.
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, path, staffSession)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostUploadAsync(staffSession, projectId, "x.pdf", SampleFiles.Pdf())).StatusCode);
        var refused = await api.SendAsync(HttpMethod.Put, path, otherEngineerSession, Edit(document, "Changed by someone else"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var refusedDelete = await api.SendAsync(HttpMethod.Delete, path, otherEngineerSession);
        Assert.Equal(HttpStatusCode.Forbidden, refusedDelete.StatusCode);

        // The uploader edits; then the manager (projects Edit) edits on top of it.
        var byUploader = await api.SendAsync(HttpMethod.Put, path, engineerSession, Edit(document, "Minutes of meeting 1"));
        await EnsureStatus(byUploader, HttpStatusCode.OK);
        var afterUploader = await Json(byUploader);
        Assert.Equal("Minutes of meeting 1", afterUploader.GetProperty("title").GetString());
        Assert.Equal("2026-01-12", afterUploader.GetProperty("documentDate").GetString());

        var byManager = await api.SendAsync(HttpMethod.Put, path, managerSession, Edit(afterUploader, "Minutes of site meeting 1"));
        await EnsureStatus(byManager, HttpStatusCode.OK);

        // The uploader's copy is now stale.
        var stale = await api.SendAsync(HttpMethod.Put, path, engineerSession, Edit(afterUploader, "Late change"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("concurrency_conflict", await CodeOf(stale));
    }

    [DatabaseFact]
    public async Task Links_point_a_document_to_records_of_its_own_project_and_filter_the_list()
    {
        await using var api = await ArchiveApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (manager, managerSession) = await api.CreateSignedInUserAsync(executive, "manager");
        var projectId = await api.CreateProjectAsync(executive, "ARC-004", manager);
        var otherProjectId = await api.CreateProjectAsync(executive, "ARC-004B", manager);

        // Real records: two daily reports here (one per author), one on the other project.
        var report = await api.CreateDailyReportAsync(managerSession, projectId);
        var secondReport = await api.CreateDailyReportAsync(executive, projectId);
        var otherReport = await api.CreateDailyReportAsync(managerSession, otherProjectId);

        var receipt = await api.UploadAsync(managerSession, projectId, "receipt.jpg", SampleFiles.Jpeg(), new()
        {
            ["title"] = "Cement receipt",
            ["category"] = "Receipt",
            ["entityType"] = "dailyreport",
            ["entityId"] = report.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        await api.UploadAsync(managerSession, projectId, "other.pdf", SampleFiles.Pdf());
        var path = $"/api/projects/{projectId}/documents/{receipt.GetProperty("id").GetInt32()}/links";

        Assert.Equal($"DailyReport:{report}", Links(receipt).Single());
        Assert.Equal(1, (await api.ListAsync(managerSession, projectId, $"&entityType=DailyReport&entityId={report}")).GetProperty("totalCount").GetInt32());

        var replaced = await api.SendAsync(HttpMethod.Put, path, managerSession, new
        {
            links = new[] { new { entityType = "DailyReport", entityId = report }, new { entityType = "DailyReport", entityId = secondReport } },
            rowVersion = receipt.GetProperty("rowVersion").GetString(),
        });
        await EnsureStatus(replaced, HttpStatusCode.OK);
        var afterReplace = await Json(replaced);
        Assert.Equal(new[] { $"DailyReport:{report}", $"DailyReport:{secondReport}" }.Order(), Links(afterReplace).Order());

        // A record of another project, or one that doesn't exist, is refused per link and changes nothing.
        var crossProject = await api.SendAsync(HttpMethod.Put, path, managerSession, new
        {
            links = new object[]
            {
                new { entityType = "DailyReport", entityId = report },
                new { entityType = "DailyReport", entityId = otherReport },
                new { entityType = "Expense", entityId = 999_999 },
            },
            rowVersion = afterReplace.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, crossProject.StatusCode);
        Assert.Equal(["links[1].entityId", "links[2].entityId"], (await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(crossProject)).Order());
        Assert.Equal(2, await api.QueryAsync(db => db.DocumentLinks.CountAsync()));

        var uploadsBefore = api.StoredFiles.Count;
        var crossUpload = await api.PostUploadAsync(managerSession, projectId, "stolen.pdf", SampleFiles.Pdf(), new()
        {
            ["title"] = "Other site report",
            ["category"] = "ProgressReport",
            ["entityType"] = "DailyReport",
            ["entityId"] = otherReport.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        Assert.Equal(HttpStatusCode.BadRequest, crossUpload.StatusCode);
        Assert.Equal(["entityId"], (await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(crossUpload)).Order());
        Assert.Equal(uploadsBefore, api.StoredFiles.Count);

        // The old rowVersion no longer works: the document row guards its links.
        var stale = await api.SendAsync(HttpMethod.Put, path, managerSession, new
        {
            links = Array.Empty<object>(),
            rowVersion = receipt.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var cleared = await api.SendAsync(HttpMethod.Put, path, managerSession, new
        {
            links = Array.Empty<object>(),
            rowVersion = afterReplace.GetProperty("rowVersion").GetString(),
        });
        await EnsureStatus(cleared, HttpStatusCode.OK);
        Assert.Empty(Links(await Json(cleared)));
        Assert.Equal(0, await api.QueryAsync(db => db.DocumentLinks.CountAsync()));
    }

    [DatabaseFact]
    public async Task Delete_hides_the_document_and_keeps_the_file()
    {
        await using var api = await ArchiveApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (manager, _) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineer, engineerSession) = await api.CreateSignedInUserAsync(executive, "engineer");
        var projectId = await api.CreateProjectAsync(executive, "ARC-005", manager, siteEngineers: [engineer]);
        var id = (await api.UploadAsync(engineerSession, projectId, "photo.png", SampleFiles.Png(), new() { ["title"] = "Gate", ["category"] = "SitePhoto" }))
            .GetProperty("id").GetInt32();
        var path = $"/api/projects/{projectId}/documents/{id}";

        var deleted = await api.SendAsync(HttpMethod.Delete, path, engineerSession);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, (await api.ListAsync(engineerSession, projectId)).GetProperty("totalCount").GetInt32());
        Assert.Empty((await api.ListAsync(engineerSession, projectId)).GetProperty("categoryCounts").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, path, engineerSession)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetContentAsync(engineerSession, projectId, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Delete, path, engineerSession)).StatusCode);

        var row = await api.QueryAsync(db => db.Documents.AsNoTracking().SingleAsync(document => document.Id == id));
        Assert.NotNull(row.DeletedAt);
        Assert.Equal(engineer, row.DeletedBy);
        Assert.Single(api.StoredFiles);
    }

    [DatabaseFact]
    public async Task A_completed_project_takes_uploads_only_from_an_executive()
    {
        await using var api = await ArchiveApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (manager, managerSession) = await api.CreateSignedInUserAsync(executive, "manager");
        var projectId = await api.CreateProjectAsync(executive, "ARC-006", manager);
        var project = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}", executive));
        await EnsureStatus(
            await api.SendAsync(HttpMethod.Put, $"/api/projects/{projectId}", executive, ProjectsApi.EditBody(project, new() { ["status"] = "Completed" })),
            HttpStatusCode.OK);

        var byManager = await api.PostUploadAsync(managerSession, projectId, "late.pdf", SampleFiles.Pdf());
        var byExecutive = await api.PostUploadAsync(executive, projectId, "handover.pdf", SampleFiles.Pdf());

        Assert.Equal(HttpStatusCode.Conflict, byManager.StatusCode);
        Assert.Equal("project_completed", await CodeOf(byManager));
        Assert.Equal(HttpStatusCode.Created, byExecutive.StatusCode);
        Assert.Single(api.StoredFiles);
    }

    private static IEnumerable<string> Links(JsonElement document) =>
        document.GetProperty("links").EnumerateArray()
            .Select(link => $"{link.GetProperty("entityType").GetString()}:{link.GetProperty("entityId").GetInt32()}");

    /// <summary>Says yes to every project id, so an upload reaches the database insert for a project that doesn't exist.</summary>
    private sealed class AnyProjectIsVisible : IProjectAccess
    {
        public Task<ProjectAccessInfo?> FindVisibleAsync(ClaimsPrincipal caller, int projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectAccessInfo?>(new ProjectAccessInfo(projectId, "GONE", "-", "-", false, null, true));
    }
}
