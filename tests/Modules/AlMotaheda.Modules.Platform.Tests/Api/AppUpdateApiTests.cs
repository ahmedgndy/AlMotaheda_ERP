using System.Net;
using System.Net.Http.Headers;
using AlMotaheda.Api.Tests;

namespace AlMotaheda.Modules.Platform.Tests.Api;

/// <summary>
/// The app-update files for the El Motahedah Updater, over real HTTP: off until configured, anonymous and read-only,
/// resumable APK downloads, and nothing outside the configured folder ever served.
/// </summary>
public sealed class AppUpdateApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-updates-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _apk = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();

    public AppUpdateApiTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "erp-android"));
        File.WriteAllText(Path.Combine(_root, "erp-android", "manifest.json"), "{\"schema\":1}");
        File.WriteAllText(Path.Combine(_root, "erp-android", "manifest.json.sig"), "c2ln");
        File.WriteAllBytes(Path.Combine(_root, "erp-android", "almotaheda-erp-1.0.0.apk"), _apk);
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "outside the channel");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task<TestApplication> StartAsync(string? root) =>
        TestApplication.StartAsync(configuration: new Dictionary<string, string?> { ["AppUpdates:RootPath"] = root });

    [Fact]
    public async Task Without_a_configured_folder_every_file_answers_404()
    {
        await using var app = await StartAsync(null);

        var response = await app.Client.GetAsync("/api/app-updates/erp-android/manifest.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_manifest_and_signature_are_served_without_signing_in_and_never_cached()
    {
        await using var app = await StartAsync(_root);

        var manifest = await app.Client.GetAsync("/api/app-updates/erp-android/manifest.json");
        var signature = await app.Client.GetAsync("/api/app-updates/erp-android/manifest.json.sig");

        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal("{\"schema\":1}", await manifest.Content.ReadAsStringAsync());
        Assert.Equal("application/json", manifest.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-cache", manifest.Headers.CacheControl?.ToString());
        Assert.Equal("c2ln", await signature.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_interrupted_apk_download_resumes_from_where_it_stopped()
    {
        await using var app = await StartAsync(_root);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/app-updates/erp-android/almotaheda-erp-1.0.0.apk");
        request.Headers.Range = new RangeHeaderValue(3000, null);

        var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("application/vnd.android.package-archive", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(_apk[3000..], await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("/api/app-updates/erp-android/..%2Fsecret.txt")]
    [InlineData("/api/app-updates/erp-android/%2E%2E%2Fsecret.txt")]
    [InlineData("/api/app-updates/..%2F/secret.txt")]
    [InlineData("/api/app-updates/ERP/manifest.json")]
    [InlineData("/api/app-updates/erp-android/.hidden")]
    [InlineData("/api/app-updates/erp-android/missing.apk")]
    public async Task Nothing_outside_the_channel_folder_or_with_an_unsafe_name_is_served(string path)
    {
        await using var app = await StartAsync(_root);

        var response = await app.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Writing_is_not_possible()
    {
        await using var app = await StartAsync(_root);

        var response = await app.Client.PutAsync("/api/app-updates/erp-android/manifest.json", new StringContent("{}"));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"schema\":1}", File.ReadAllText(Path.Combine(_root, "erp-android", "manifest.json")));
    }
}
