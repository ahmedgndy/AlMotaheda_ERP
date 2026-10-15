using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AlMotaheda.Modules.Archive.Storage;

/// <summary>
/// Readiness check <c>archive-storage</c> on <c>/health</c>: writes and removes a tiny file in the archive folder.
/// A server that can't store uploads should leave the load balancer, like one that can't reach the database.
/// </summary>
/// <remarks>Registered by <c>ArchiveModule</c> itself, with a 5-second timeout. It never reads or lists real files.</remarks>
internal sealed class ArchiveStorageHealthCheck(IFileStore store) : IHealthCheck
{
    public const string Name = "archive-storage";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await store.ProbeAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (StorageUnavailableException exception)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "The archive storage folder can't be written.", exception);
        }
    }
}
