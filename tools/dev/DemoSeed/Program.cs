using System.Security.Cryptography;
using AlMotaheda.DemoSeed;

// Development-only demo data. Run with the demo API up (tools/dev/run-demo-api.sh), then:
//
//   tools/dev/seed-demo.sh
//
// Every step looks for what it would create first (by e-mail, code, plate, idempotency key or a "[DEMO …]" marker),
// so running it again creates nothing twice and never deletes or resets anything. It refuses any API that is not on
// this machine.

var root = FindRoot();
var env = EnvFile.Read(Path.Combine(root, ".local", "demo.env"));
var baseUrl = env.GetValueOrDefault("ASPNETCORE_URLS", "https://localhost:7200").Split(';')[0];
if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || !(baseUri.IsLoopback || baseUri.Host == "localhost"))
{
    Console.Error.WriteLine($"Refusing: the demo seed only talks to an API on this machine (got {baseUrl}).");
    return 1;
}

var accountsFile = Path.Combine(root, ".local", "demo-accounts.env");
var accounts = EnvFile.Read(accountsFile);
if (!accounts.ContainsKey("DEMO_PASSWORD"))
{
    // One development-only password for every demo account, made once and kept for this machine.
    accounts["DEMO_PASSWORD"] = $"Demo-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}-2026";
    EnvFile.Write(accountsFile, accounts, "Development-only passwords of the demo accounts (git-ignored). Keep this file to keep signing in.");
}

var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromMinutes(5) };
var health = await http.GetAsync("/health");
if (!health.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"The API at {baseUri} is not healthy ({(int)health.StatusCode}). Start it with tools/dev/run-demo-api.sh.");
    return 1;
}

var demo = new Demo(http, env, accounts, accountsFile, DateOnly.FromDateTime(DateTime.Today));
try
{
    await demo.RunAsync();
}
catch (ApiError error)
{
    Console.Error.WriteLine($"\nStopped: {error.Message}");
    return 2;
}

Console.WriteLine($"\nDone: {Log.Created} created, {Log.Reused} already there. Accounts: .local/demo-credentials.md");
return 0;

static string FindRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AlMotahedaERP.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException("Run from inside the repository.");
}

internal static class EnvFile
{
    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>();
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || !trimmed.Contains('='))
            {
                continue;
            }

            var index = trimmed.IndexOf('=');
            values[trimmed[..index]] = trimmed[(index + 1)..].Trim('\'', '"');
        }

        return values;
    }

    public static void Write(string path, IReadOnlyDictionary<string, string> values, string header)
    {
        var lines = new List<string> { $"# {header}" };
        lines.AddRange(values.Select(pair => $"{pair.Key}='{pair.Value}'"));
        File.WriteAllLines(path, lines);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
