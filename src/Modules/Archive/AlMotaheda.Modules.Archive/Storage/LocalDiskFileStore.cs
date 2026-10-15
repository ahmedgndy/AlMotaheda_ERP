using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AlMotaheda.BuildingBlocks.Time;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Archive.Storage;

/// <summary>
/// Keeps the archive's files in a folder on the server's disk (<c>Archive:Storage:RootPath</c>), as
/// <c>{projectId}/{yyyy}/{MM}/{guid}</c> with no extension, so nothing in the folder can be run or served by type.
/// </summary>
/// <remarks>
/// <para>A save writes to a temporary name next to the final one and renames it only when the content is complete,
/// so a half-written file never appears under a key. The content is copied in 80 KB pieces while the hash and the
/// size are computed: a file is never held in memory.</para>
/// <para>Stateless, so it is a singleton. With two or more servers, the folder must be shared storage, or this
/// class is replaced by a cloud store behind <see cref="IFileStore"/>.</para>
/// </remarks>
internal sealed partial class LocalDiskFileStore(IOptions<ArchiveStorageOptions> options, CompanyClock clock) : IFileStore
{
    private const int BufferSize = 81_920;
    private const string TemporarySuffix = ".partial";

    private readonly string _root = options.Value.RootPath
        ?? throw new InvalidOperationException("Archive:Storage:RootPath is not configured.");

    public async Task<StoredFile> SaveAsync(int projectId, Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(projectId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var now = clock.UtcNow;
        var storageKey = $"{projectId}/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}";
        var path = PathOf(storageKey);
        var temporaryPath = path + TemporarySuffix;

        Write(() => Directory.CreateDirectory(Path.GetDirectoryName(path)!));

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;

            await using (var target = Write(() => OpenNew(temporaryPath)))
            {
                // A failed read comes from the request (the client dropped the upload) and passes through as it is.
                // A failed write is the disk's fault and becomes StorageUnavailableException.
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                {
                    size += read;
                    if (size > maxBytes)
                    {
                        throw new FileTooLargeException(maxBytes);
                    }

                    hash.AppendData(buffer, 0, read);
                    await WriteAsync(() => target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).AsTask());
                }

                // The bytes must be on the disk before the database row says they exist.
                await WriteAsync(async () =>
                {
                    await target.FlushAsync(cancellationToken);
                    target.Flush(flushToDisk: true);
                });
            }

            Write(() =>
            {
                File.Move(temporaryPath, path);
                return true;
            });

            return new StoredFile(storageKey, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathOf(storageKey);

        try
        {
            Stream stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = BufferSize,
            });
            return Task.FromResult<Stream?>(stream);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StorageUnavailableException("The archive folder can't be read.", exception);
        }
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(PathOf(storageKey));
        return Task.CompletedTask;
    }

    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        var probe = Path.Combine(_root, $".health-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(probe, [0], cancellationToken);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(probe);
            throw new StorageUnavailableException("The archive folder can't be written.", exception);
        }
    }

    /// <summary>
    /// The full path of a key. Only keys this class makes are accepted, and the result must stay inside the root,
    /// so a damaged database value can never point outside the archive folder.
    /// </summary>
    internal string PathOf(string storageKey)
    {
        if (!KeyPattern().IsMatch(storageKey))
        {
            throw new ArgumentException("The storage key is not valid.", nameof(storageKey));
        }

        var path = Path.GetFullPath(Path.Combine(_root, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException("The storage key is not valid.", nameof(storageKey));
        }

        return path;
    }

    private static FileStream OpenNew(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        BufferSize = 0,
    });

    private static T Write<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StorageUnavailableException("The archive folder can't be written.", exception);
        }
    }

    private static async Task WriteAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StorageUnavailableException("The archive folder can't be written.", exception);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing else can be done here; the health check reports a folder that can't be written.
        }
    }

    [GeneratedRegex("^[1-9][0-9]{0,9}/[0-9]{4}/[0-9]{2}/[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
