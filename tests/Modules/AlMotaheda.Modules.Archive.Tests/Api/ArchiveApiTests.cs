using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using AlMotaheda.Modules.Archive.Storage;
using AlMotaheda.Modules.Archive.Tests.Unit;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Archive.Tests.Api;

/// <summary>
/// The archive endpoints over real HTTP without SQL Server: sign-in, permissions, project scope (404), validation
/// (400), and the upload checks that run before the database save, including "a failed save leaves no file".
/// </summary>
/// <remarks>
/// Each test gets its own storage folder (<c>Archive:Storage:RootPath</c>), so it can check that nothing was left
/// on the disk.
/// </remarks>
public sealed class ArchiveApiTests : IDisposable
{
    private const string Executive = "executive";
    private const string Manager = "manager";
    private const string RowVersion = "AAAAAAAAB9E=";

    private readonly TemporaryFolder _storage = new();

    /// <summary>Every archive endpoint, with the permission letters it needs (CE = Create or Edit).</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        { "GET", "/api/projects/1/documents", "V" },
        { "GET", "/api/projects/1/documents/2", "V" },
        { "GET", "/api/projects/1/documents/2/content", "V" },
        { "POST", "/api/projects/1/documents", "CE" },
        { "PUT", "/api/projects/1/documents/2", "V" },
        { "PUT", "/api/projects/1/documents/2/links", "V" },
        { "DELETE", "/api/projects/1/documents/2", "V" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_a_token_get_401(string method, string path, string letters)
    {
        _ = letters;
        await using var app = await StartAsync();

        var response = await SendAsync(app.App, method, path, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await CodeOf(response));
        Assert.Empty(_storage.Files());
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_the_permission_get_403(string method, string path, string letters)
    {
        await using var app = await StartAsync(new VisibleProject());
        var others = new string("VCEAF".Where(letter => !letters.Contains(letter)).ToArray());
        var token = TestTokens.Create(role: Manager, permissions: [("projects", others), ("payments", "VCEAF")]);

        var response = await SendAsync(app.App, method, path, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
        Assert.Empty(_storage.Files());
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_project_the_caller_cannot_see_answers_404_on_every_endpoint_including_the_file_bytes(string method, string path, string letters)
    {
        _ = letters;
        await using var app = await StartAsync();
        var token = TestTokens.Create(role: Manager, permissions: [("projects", "VCEAF")]);

        var response = await SendAsync(app.App, method, path, token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await CodeOf(response));
        Assert.Empty(_storage.Files());
    }

    [Fact]
    public async Task Upload_is_open_to_projects_create_or_projects_edit()
    {
        await using var app = await StartAsync();
        var creator = TestTokens.Create(role: "engineer", permissions: [("projects", "C")]);
        var editor = TestTokens.Create(role: Manager, permissions: [("projects", "E")]);

        // Both get past the permission check to the project scope (404 on the stand-in database).
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(app.App, "POST", "/api/projects/1/documents", creator)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(app.App, "POST", "/api/projects/1/documents", editor)).StatusCode);
    }

    [Theory]
    [InlineData("PUT", "/api/projects/1/documents/2", "category,rowVersion,title")]
    [InlineData("PUT", "/api/projects/1/documents/2/links", "links,rowVersion")]
    public async Task Empty_forms_list_their_required_fields(string method, string path, string fields)
    {
        await using var app = await StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("projects", "VCEAF")]);

        var response = await Send(app.App.Client, method, path, token, JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeOf(response));
        Assert.Equal(fields.Split(','), (await ErrorFields(response)).Order());
        Assert.Empty(app.Database.Commands);
    }

    [Fact]
    public async Task A_links_form_reports_each_bad_link_before_any_database_work()
    {
        await using var app = await StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("projects", "VCEAF")]);

        var response = await Send(app.App.Client, "PUT", "/api/projects/1/documents/2/links", token, JsonContent.Create(new
        {
            links = new object[] { new { entityType = "Expense", entityId = 4 }, new { entityType = "Truck", entityId = 0 } },
            rowVersion = "not-base64",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["links[1].entityId", "links[1].entityType", "rowVersion"], (await ErrorFields(response)).Order());
        Assert.Empty(app.Database.Commands);
    }

    [Theory]
    [InlineData("/api/projects/1/documents?category=Photos", "category")]
    [InlineData("/api/projects/1/documents?kind=Excel", "kind")]
    [InlineData("/api/projects/1/documents?from=2026-13-01", "from")]
    [InlineData("/api/projects/1/documents?from=2026-02-01&to=2026-01-01", "to")]
    [InlineData("/api/projects/1/documents?uploadedBy=0", "uploadedBy")]
    [InlineData("/api/projects/1/documents?entityType=Expense", "entityId")]
    [InlineData("/api/projects/1/documents?entityId=3", "entityType")]
    [InlineData("/api/projects/1/documents?entityType=Truck&entityId=3", "entityType")]
    [InlineData("/api/projects/1/documents?pageSize=101", "pageSize")]
    [InlineData("/api/projects/1/documents?page=0", "page")]
    [InlineData("/api/projects/1/documents/2/content?disposition=embed", "disposition")]
    public async Task List_and_content_parameters_are_validated(string path, string field)
    {
        await using var app = await StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("projects", "VCEAF")]);

        var response = await SendAsync(app.App, "GET", path, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await ErrorFields(response));
        Assert.Empty(app.Database.Commands);
    }

    [Fact]
    public async Task The_list_reads_one_project_without_deleted_documents_and_with_the_counts()
    {
        await using var app = await StartAsync(new VisibleProject());
        var token = TestTokens.Create(role: Executive, permissions: [("projects", "V")]);

        var response = await SendAsync(
            app.App, "GET", "/api/projects/1/documents?category=sitephoto&kind=image&search=slab&from=2026-01-01&to=2026-01-31&entityType=dailyReport&entityId=5", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("categoryCounts").ValueKind);
        var archiveSql = app.Database.Commands.Where(sql => sql.Contains("[docs].[Document]", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, archiveSql.Count);
        Assert.All(archiveSql, sql =>
        {
            Assert.Contains("[ProjectId] = @", sql);
            Assert.Contains("[DeletedAt] IS NULL", sql);
        });
    }

    [Fact]
    public async Task A_document_that_does_not_exist_answers_404_on_read_content_edit_links_and_delete()
    {
        await using var app = await StartAsync(new VisibleProject());
        var token = TestTokens.Create(role: Executive, permissions: [("projects", "VCEAF")]);

        foreach (var (method, path) in new[]
                 {
                     ("GET", "/api/projects/1/documents/2"),
                     ("GET", "/api/projects/1/documents/2/content?disposition=inline"),
                     ("PUT", "/api/projects/1/documents/2"),
                     ("PUT", "/api/projects/1/documents/2/links"),
                     ("DELETE", "/api/projects/1/documents/2"),
                 })
        {
            var response = await SendAsync(app.App, method, path, token);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("not_found", await CodeOf(response));
        }
    }

    [Theory]
    [InlineData("POST", "/api/projects/1/documents")]
    [InlineData("PUT", "/api/projects/1/documents/2")]
    [InlineData("PUT", "/api/projects/1/documents/2/links")]
    [InlineData("DELETE", "/api/projects/1/documents/2")]
    public async Task A_completed_project_takes_no_change_from_anyone_but_an_executive(string method, string path)
    {
        await using var app = await StartAsync(new VisibleProject(IsCompleted: true));
        var token = TestTokens.Create(role: Manager, permissions: [("projects", "VCEAF")]);

        var response = await SendAsync(app.App, method, path, token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("project_completed", await CodeOf(response));
        Assert.Empty(_storage.Files());
    }

    [Fact]
    public async Task An_upload_that_is_not_a_multipart_form_answers_file_required()
    {
        await using var app = await StartAsync(new VisibleProject());

        var response = await Send(app.App.Client, "POST", "/api/projects/1/documents", Uploader(), JsonContent.Create(new { title = "x" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("file_required", await CodeOf(response));
        Assert.Equal(["file"], await ErrorFields(response));
    }

    [Fact]
    public async Task A_form_without_a_file_answers_file_required()
    {
        await using var app = await StartAsync(new VisibleProject());

        var response = await UploadAsync(app, file: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("file_required", await CodeOf(response));
    }

    [Fact]
    public async Task Every_failing_field_is_reported_at_once_and_the_stored_file_is_removed()
    {
        await using var app = await StartAsync(new VisibleProject());

        var response = await UploadAsync(app, ("certificate.pdf", SampleFiles.Pdf()), new()
        {
            ["title"] = " ",
            ["category"] = "Photos",
            ["documentDate"] = "2999-01-01",
            ["entityType"] = "Expense",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeOf(response));
        Assert.Equal(["category", "documentDate", "entityId", "title"], (await ErrorFields(response)).Order());
        Assert.Empty(_storage.Files());
    }

    [Fact]
    public async Task Missing_fields_and_a_missing_file_are_reported_together()
    {
        await using var app = await StartAsync(new VisibleProject());

        var response = await UploadAsync(app, file: null, new() { ["title"] = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeOf(response));
        Assert.Equal(["category", "file", "title"], (await ErrorFields(response)).Order());
    }

    [Fact]
    public async Task An_executable_renamed_to_pdf_is_refused_and_never_stored()
    {
        await using var app = await StartAsync(new VisibleProject());

        var response = await UploadAsync(app, ("invoice.pdf", SampleFiles.Exe()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("file_type_not_allowed", await CodeOf(response));
        Assert.Equal(["file"], await ErrorFields(response));
        Assert.Empty(_storage.Files());
    }

    [Theory]
    [InlineData("photo.png", "jpeg")]
    [InlineData("minutes.xlsx", "docx")]
    [InlineData("drawing.pdf", "dwg")]
    public async Task A_name_that_does_not_match_the_content_is_refused(string fileName, string sample)
    {
        await using var app = await StartAsync(new VisibleProject());

        var response = await UploadAsync(app, (fileName, FileTypesTests.Sample(sample)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("file_extension_mismatch", await CodeOf(response));
        Assert.Empty(_storage.Files());
    }

    [Fact]
    public async Task A_zip_named_like_a_word_file_is_refused_and_removed()
    {
        await using var app = await StartAsync(new VisibleProject());

        var response = await UploadAsync(app, ("minutes.docx", SampleFiles.PlainZip()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("file_type_not_allowed", await CodeOf(response));
        Assert.Empty(_storage.Files());
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        await using var app = await StartAsync(new VisibleProject());

        var response = await UploadAsync(app, ("empty.pdf", Array.Empty<byte>()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("file_empty", await CodeOf(response));
        Assert.Empty(_storage.Files());
    }

    [Fact]
    public async Task An_image_over_its_limit_answers_413_and_nothing_is_kept()
    {
        await using var app = await StartAsync(new VisibleProject(), new() { ["Archive:Limits:ImageMaxMegabytes"] = "1" });

        var response = await UploadAsync(app, ("site.jpg", SampleFiles.Jpeg(1_300_000)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("file_too_large", await CodeOf(response));
        Assert.Empty(_storage.Files());
    }

    [Fact]
    public async Task The_limit_follows_the_kind_so_a_pdf_of_the_same_size_passes_the_checks()
    {
        await using var app = await StartAsync(new VisibleProject(), new() { ["Archive:Limits:ImageMaxMegabytes"] = "1" });

        // Past every file check, the save fails on the stand-in database (500): the stored file must not stay.
        var response = await UploadAsync(app, ("report.pdf", SampleFiles.Pdf(1_300_000)));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(_storage.Files());
    }

    [Theory]
    [InlineData("IMG_0001.HEIC", "heic")]
    [InlineData("IMG_0002.JPG", "cameraJpeg")]
    [InlineData("blob", "cameraJpeg")]
    [InlineData("IMG_0003.MOV", "mov")]
    [InlineData("VID_20260115.mp4", "mp4")]
    [InlineData("boq.xlsx", "xlsx")]
    [InlineData("plan.dwg", "dwg")]
    public async Task When_the_database_save_fails_the_stored_file_is_removed(string fileName, string sample)
    {
        await using var app = await StartAsync(new VisibleProject());

        // Phone photos and videos pass every check like any other upload; the stand-in database can't save.
        var response = await UploadAsync(app, (fileName, FileTypesTests.Sample(sample)));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(app.Database.Commands, sql => sql.Contains("INSERT INTO [docs].[Document]", StringComparison.Ordinal));
        Assert.Empty(_storage.Files());
    }

    [Fact]
    public async Task A_video_larger_than_the_server_default_request_size_is_accepted_by_the_upload_endpoint()
    {
        await using var app = await StartAsync(new VisibleProject());

        // Kestrel refuses bodies over about 28.6 MB unless the endpoint raises its limit.
        var response = await UploadAsync(app, ("site-walk.mp4", SampleFiles.Mp4(31 * 1024 * 1024)));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(app.Database.Commands, sql => sql.Contains("INSERT INTO [docs].[Document]", StringComparison.Ordinal));
        Assert.Empty(_storage.Files());
    }

    [Fact]
    public async Task Other_endpoints_keep_the_server_default_request_size()
    {
        await using var app = await StartAsync(new VisibleProject());
        var token = TestTokens.Create(role: Executive, permissions: [("projects", "VCEAF")]);
        var huge = new string('x', 31 * 1024 * 1024);

        var response = await Send(app.App.Client, "PUT", "/api/projects/1/documents/2", token, JsonContent.Create(new { title = huge }));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task The_storage_health_check_is_part_of_readiness()
    {
        await using var app = await StartAsync();

        var response = await app.App.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var check = (await Json(response)).GetProperty("checks").EnumerateArray()
            .Single(entry => entry.GetProperty("name").GetString() == ArchiveStorageHealthCheck.Name);
        Assert.Equal("Healthy", check.GetProperty("status").GetString());
        Assert.Empty(_storage.Files());
    }

    [Fact]
    public async Task A_storage_folder_that_cannot_be_written_makes_readiness_unhealthy()
    {
        await using var app = await StartAsync(store: new UnavailableStore());

        var response = await app.App.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var check = (await Json(response)).GetProperty("checks").EnumerateArray()
            .Single(entry => entry.GetProperty("name").GetString() == ArchiveStorageHealthCheck.Name);
        Assert.Equal("Unhealthy", check.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_upload_to_a_store_that_cannot_be_written_answers_503()
    {
        await using var app = await StartAsync(new VisibleProject(), store: new UnavailableStore());

        var response = await UploadAsync(app, ("certificate.pdf", SampleFiles.Pdf()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("storage_unavailable", await CodeOf(response));
    }

    [Fact]
    public async Task The_api_refuses_to_start_when_the_storage_folder_cannot_be_created()
    {
        var blocked = Path.Combine(_storage.Path, "a-file");
        await File.WriteAllTextAsync(blocked, "x");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => TestApplication.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["Archive:Storage:RootPath"] = Path.Combine(blocked, "archive"),
        }));

        var validation = Assert.IsType<Microsoft.Extensions.Options.OptionsValidationException>(error is AggregateException aggregate ? aggregate.InnerException : error);
        Assert.Contains("Archive:Storage:RootPath", validation.Message);
    }

    [Fact]
    public async Task In_production_the_storage_folder_must_be_set()
    {
        // Set to empty on purpose: the shared test host may give every test a storage folder by default.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => TestApplication.StartAsync(
            environment: "Production",
            configuration: new Dictionary<string, string?> { ["Archive:Storage:RootPath"] = "" }));

        var validation = Assert.IsType<Microsoft.Extensions.Options.OptionsValidationException>(error is AggregateException aggregate ? aggregate.InnerException : error);
        Assert.Contains("Archive:Storage:RootPath is missing", validation.Message);
    }

    public void Dispose() => _storage.Dispose();

    private static string Uploader() => TestTokens.Create(role: "engineer", permissions: [("projects", "VC")]);

    private Task<HttpResponseMessage> UploadAsync(TestHost app, (string Name, byte[] Bytes)? file, Dictionary<string, string>? fields = null)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in fields ?? new Dictionary<string, string> { ["title"] = "Payment certificate 7", ["category"] = "PaymentCertificate" })
        {
            form.Add(new StringContent(value), name);
        }

        if (file is { } part)
        {
            var content = new ByteArrayContent(part.Bytes);

            // What a browser sends for any file; the server never trusts it.
            content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(content, "file", part.Name);
        }

        return Send(app.App.Client, "POST", "/api/projects/1/documents", Uploader(), form);
    }

    /// <summary>Sends a request with a body each endpoint accepts, so tests reach the checks after validation.</summary>
    private static Task<HttpResponseMessage> SendAsync(TestApplication app, string method, string path, string? token)
    {
        HttpContent? body = method switch
        {
            "POST" => ValidUpload(),
            "PUT" when path.EndsWith("/links", StringComparison.Ordinal) => JsonContent.Create(new
            {
                links = new[] { new { entityType = "Expense", entityId = 4 } },
                rowVersion = RowVersion,
            }),
            "PUT" => JsonContent.Create(new { title = "Minutes of meeting 3", category = "MeetingMinutes", rowVersion = RowVersion }),
            _ => null,
        };

        return Send(app.Client, method, path, token, body);
    }

    private static MultipartFormDataContent ValidUpload() => new()
    {
        { new StringContent("Payment certificate 7"), "title" },
        { new StringContent("PaymentCertificate"), "category" },
        { new ByteArrayContent(SampleFiles.Pdf()), "file", "certificate-7.pdf" },
    };

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string path, string? token, HttpContent? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client.SendAsync(request);
    }

    private async Task<TestHost> StartAsync(IProjectAccess? access = null, Dictionary<string, string?>? settings = null, IFileStore? store = null)
    {
        var configuration = new Dictionary<string, string?> { ["Archive:Storage:RootPath"] = _storage.Path };
        foreach (var (key, value) in settings ?? [])
        {
            configuration[key] = value;
        }

        var database = new StandInDatabase();
        var app = await TestApplication.StartAsync(configuration: configuration, configureBuilder: builder =>
        {
            database.AddTo(builder);
            if (access is not null)
            {
                builder.Services.AddScoped(_ => access);
            }

            if (store is not null)
            {
                builder.Services.AddSingleton<IFileStore>(store);
            }
        });

        return new TestHost(app, database);
    }

    private sealed record TestHost(TestApplication App, StandInDatabase Database) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>Says every project is visible, so tests reach the rules that come after the scope check.</summary>
    private sealed record VisibleProject(bool IsCompleted = false, bool Executive = false) : IProjectAccess
    {
        public Task<ProjectAccessInfo?> FindVisibleAsync(ClaimsPrincipal caller, int projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectAccessInfo?>(new ProjectAccessInfo(
                projectId, "P-1", "مشروع", "Project", IsCompleted, ProjectResponsibilities.ProjectManager, Executive));
    }

    /// <summary>A store whose disk is full or gone.</summary>
    private sealed class UnavailableStore : IFileStore
    {
        public Task<StoredFile> SaveAsync(int projectId, Stream content, long maxBytes, CancellationToken cancellationToken) =>
            throw new StorageUnavailableException("The disk is full.");

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
            throw new StorageUnavailableException("The disk is gone.");

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ProbeAsync(CancellationToken cancellationToken) => throw new StorageUnavailableException("The disk is full.");
    }
}
