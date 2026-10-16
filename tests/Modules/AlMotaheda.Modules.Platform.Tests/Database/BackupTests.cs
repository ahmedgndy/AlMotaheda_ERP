using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Platform.DatabaseBackup;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Platform.Tests.Database;

/// <summary>
/// Real backups on SQL Server (in Docker): BACKUP WITH CHECKSUM and RESTORE VERIFYONLY run for real; the folder is
/// inside the SQL Server container, so the disk side (zip, delete) is a stand-in that records what it was asked.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class BackupTests(SqlServerFixture database)
{
    private const string ContainerFolder = "/var/opt/mssql/data";

    private sealed class RecordingFiles : IBackupFiles
    {
        public ConcurrentBag<string> Deleted { get; } = [];

        public void EnsureFolder(string folder)
        {
        }

        public long? ZipFolder(string sourceFolder, string zipPath) => 1_234;

        public void Delete(string path) => Deleted.Add(path);
    }

    [DatabaseFact]
    public async Task Backups_are_verified_kept_to_the_count_and_a_failure_tells_the_executives()
    {
        var files = new RecordingFiles();
        await using var api = await AccessApi.StartAsync(
            database,
            new Dictionary<string, string?> { ["Platform:Backup:AllowNonWindows"] = "true", ["Platform:Backup:PollSeconds"] = "1" },
            builder => builder.Services.AddSingleton<IBackupFiles>(files));
        var executive = await api.SignInExecutiveAsync();
        var (_, manager) = await api.CreateSignedInUserAsync(executive, "manager");

        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, "/api/settings/backup/run", manager)).StatusCode);

        var settings = await Ok(api, executive, HttpMethod.Get, "/api/settings/backup");
        Assert.Equal((3, "02:00"), (settings.GetProperty("retentionCount").GetInt32(), settings.GetProperty("dailyAt").GetString()));
        settings = await Ok(api, executive, HttpMethod.Put, "/api/settings/backup", new
        {
            enabled = false, dailyAt = "02:00", folder = ContainerFolder, retentionCount = 2, includeDocuments = true,
            rowVersion = settings.GetProperty("rowVersion").GetString(),
        });

        // Three backups now: each checked, sized, and with the documents zipped next to it.
        var manual = new List<JsonElement>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await api.SendAsync(HttpMethod.Post, "/api/settings/backup/run", executive)).StatusCode);
            manual.Add(await WaitForManualRunAsync(api, executive, manual.Count + 1));
        }

        Assert.All(manual, run =>
        {
            Assert.Equal(("Succeeded", true), (run.GetProperty("status").GetString(), run.GetProperty("verified").GetBoolean()));
            Assert.True(run.GetProperty("sizeBytes").GetInt64() > 0);
            Assert.Matches(@"_Full_\d{8}_\d{6}\.bak$", run.GetProperty("fileName").GetString());
            Assert.Equal(1_234, run.GetProperty("documentsSizeBytes").GetInt64());
        });

        // Keep two: only the oldest was removed, both its files, after the newest was verified.
        var history = await ManualRunsAsync(api, executive);
        var oldest = history.Single(run => run.GetProperty("id").GetInt64() == manual[0].GetProperty("id").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, oldest.GetProperty("filesDeletedAt").ValueKind);
        Assert.Equal(2, files.Deleted.Count);
        Assert.All(files.Deleted, path => Assert.Contains(Path.GetFileNameWithoutExtension(manual[0].GetProperty("fileName").GetString()!)[..^22], path));
        Assert.Equal(2, history.Count(run => run.GetProperty("filesDeletedAt").ValueKind == JsonValueKind.Null && run.GetProperty("verified").GetBoolean()));

        // A folder SQL Server can't write: the run fails, nothing is removed, the Executive is told.
        settings = await Ok(api, executive, HttpMethod.Get, "/api/settings/backup");
        await Ok(api, executive, HttpMethod.Put, "/api/settings/backup", new
        {
            enabled = false, dailyAt = "02:00", folder = "/no/such/folder", retentionCount = 2, rowVersion = settings.GetProperty("rowVersion").GetString(),
        });
        await api.SendAsync(HttpMethod.Post, "/api/settings/backup/run", executive);
        var failed = await WaitForManualRunAsync(api, executive, 4);
        Assert.Equal(("Failed", false), (failed.GetProperty("status").GetString(), failed.GetProperty("verified").GetBoolean()));
        Assert.False(string.IsNullOrEmpty(failed.GetProperty("error").GetString()));
        Assert.Equal(2, files.Deleted.Count);

        var notifications = await Ok(api, executive, HttpMethod.Get, "/api/notifications");
        Assert.Contains(notifications.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("type").GetString() == "BackupFailed" && item.GetProperty("entityKey").GetString() == failed.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<JsonElement> Ok(AccessApi api, Session session, HttpMethod method, string path, object? body = null)
    {
        var response = await api.SendAsync(method, path, session, body);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    private static async Task<List<JsonElement>> ManualRunsAsync(AccessApi api, Session executive) =>
        (await Ok(api, executive, HttpMethod.Get, "/api/backups?pageSize=100")).GetProperty("items").EnumerateArray()
            .Where(run => run.GetProperty("trigger").GetString() == "Manual").ToList();

    /// <summary>Waits until the <paramref name="count"/>-th manual run has finished.</summary>
    private static async Task<JsonElement> WaitForManualRunAsync(AccessApi api, Session executive, int count)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var runs = await ManualRunsAsync(api, executive);
            if (runs.Count >= count && runs[0].GetProperty("status").GetString() != "Running")
            {
                return runs[0];
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("Waited 60 seconds for the backup.");
    }
}
