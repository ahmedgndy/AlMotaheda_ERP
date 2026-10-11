using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace AlMotaheda.Modules.Access.Tests.Database;

/// <summary>
/// One SQL Server container for every database test (Testcontainers). Each test gets its own new database
/// inside it, created by the API's own startup migration, so tests never share data.
/// </summary>
/// <remarks>
/// Needs Docker. On a Mac with Apple Silicon, Docker Desktop runs the SQL Server image with Rosetta
/// ("Use Rosetta for x86_64/amd64 emulation" in Docker Desktop settings).
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string Image = "mcr.microsoft.com/mssql/server:2022-latest";

    private MsSqlContainer? _container;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        _container = new MsSqlBuilder().WithImage(Image).Build();
        await _container.StartAsync();
    }

    /// <summary>A connection string to a database that doesn't exist yet; the API's migration creates it.</summary>
    public string NewDatabaseConnectionString()
    {
        if (_container is null)
        {
            throw new InvalidOperationException("The SQL Server container isn't running.");
        }

        return new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = $"access_test_{Guid.NewGuid():N}",
            TrustServerCertificate = true,
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}

/// <summary>A test that needs SQL Server in Docker. Skipped, with the reason, when Docker isn't running.</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = "Docker isn't running, so the SQL Server tests are skipped. Start Docker and run the tests again.";
        }
    }
}

internal static class DockerAvailability
{
    private static readonly Lazy<bool> Available = new(Detect);

    public static bool IsAvailable => Available.Value;

    private static bool Detect()
    {
        if (Environment.GetEnvironmentVariable("SKIP_DATABASE_TESTS") is "1" or "true")
        {
            return false;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")) || OperatingSystem.IsWindows())
        {
            return true;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] sockets =
        [
            "/var/run/docker.sock",
            Path.Combine(home, ".docker", "run", "docker.sock"),
            Path.Combine(home, ".colima", "default", "docker.sock"),
            Path.Combine(home, ".rd", "docker.sock"),
        ];

        return sockets.Any(File.Exists);
    }
}
