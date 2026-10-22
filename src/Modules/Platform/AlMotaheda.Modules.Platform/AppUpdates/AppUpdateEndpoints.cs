using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Platform.AppUpdates;

/// <summary>
/// Where the published app updates are (<c>AppUpdates</c> section). Off until <see cref="RootPath"/> is set: then every
/// request answers 404.
/// </summary>
internal sealed class AppUpdateOptions
{
    public const string SectionName = "AppUpdates";

    /// <summary>A folder with one sub-folder per channel (<c>erp-android/manifest.json</c>, <c>…/manifest.json.sig</c>, <c>…/*.apk</c>).</summary>
    public string? RootPath { get; set; }
}

/// <summary>
/// <c>GET /api/app-updates/{channel}/{file}</c>: the files the El Motahedah Updater downloads (the signed manifest, its
/// signature, the APK), anonymous and read-only. The updater trusts a file only through the manifest signature and
/// the APK checksum it carries, never because it came from here. Only plain names inside the configured folder are
/// served: no sub-paths, no "..", nothing outside it. APK downloads can resume (HTTP Range).
/// </summary>
internal static partial class AppUpdateEndpoints
{
    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex ChannelPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$")]
    private static partial Regex FilePattern();

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/app-updates/{channel}/{file}", Serve)
            .AllowAnonymous()
            .WithTags("Platform")
            .WithName("GetAppUpdateFile")
            .WithSummary("A published app-update file (signed manifest, signature, APK). Anonymous; trust comes from the manifest signature.");
    }

    internal static Results<PhysicalFileHttpResult, NotFound> Serve(string channel, string file, HttpContext httpContext, IOptions<AppUpdateOptions> options)
    {
        if (Resolve(options.Value.RootPath, channel, file) is not { } path)
        {
            return TypedResults.NotFound();
        }

        var manifest = !file.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
        httpContext.Response.Headers.CacheControl = manifest ? "no-cache" : "public, max-age=31536000, immutable";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var contentType = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".apk" => "application/vnd.android.package-archive",
            _ => "text/plain",
        };
        return TypedResults.PhysicalFile(path, contentType, enableRangeProcessing: !manifest);
    }

    /// <summary>The full path of an existing file of the channel, or <c>null</c> (off, a bad name, outside the folder, missing).</summary>
    internal static string? Resolve(string? rootPath, string channel, string file)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !ChannelPattern().IsMatch(channel) || !FilePattern().IsMatch(file) || file.Contains(".."))
        {
            return null;
        }

        var root = Path.GetFullPath(Path.Combine(rootPath, channel)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, file));
        return path.StartsWith(root, StringComparison.Ordinal) && File.Exists(path) ? path : null;
    }
}
