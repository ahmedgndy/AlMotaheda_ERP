using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AlMotaheda.BuildingBlocks.Time;

namespace AlMotaheda.BuildingBlocks.Storage;

/// <summary>
/// A private folder of evidence files (photos, receipts, PDFs) that a module keeps outside the database and only ever
/// serves through its own API after a permission check. The database keeps the metadata and the key. One store per
/// module (vehicle evidence, HR request evidence), the same code for each: the same key shape, the same atomic
/// write, the same type detection from the bytes.
/// </summary>
/// <remarks>An interface because storage is an external boundary: tests replace it, a cloud store can replace the disk.</remarks>
public interface IPrivateFileStore
{
    /// <summary>Streams the content into a new file, computing its size and SHA-256. The file appears only once complete.</summary>
    /// <exception cref="FileTooLargeException">The content is longer than <paramref name="maxBytes"/>.</exception>
    /// <exception cref="StorageUnavailableException">The store can't be written.</exception>
    Task<StoredFile> SaveAsync(Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>A read-only stream of the file, or <c>null</c> when it isn't there.</summary>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Removes a file that was stored but never recorded (a failed save). Nothing happens when it is gone.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}

public sealed record StoredFile(string StorageKey, long SizeBytes, string Sha256);

public sealed class FileTooLargeException(long maxBytes) : Exception($"The file is larger than the limit of {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}

public sealed class StorageUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Keeps files in a private folder as <c>{prefix}/{yyyy}/{MM}/{guid}</c> with no extension, so nothing in the folder
/// can be run or served by type. <paramref name="prefix"/> is the module's own (for example <c>fleet</c>, <c>hr</c>),
/// so two modules never accept each other's keys.
/// </summary>
public sealed partial class LocalPrivateFileStore(string root, string prefix, CompanyClock clock) : IPrivateFileStore
{
    private const int BufferSize = 81_920;
    private const string TemporarySuffix = ".partial";

    private readonly string _root = string.IsNullOrWhiteSpace(root) ? throw new InvalidOperationException("The private storage folder is not configured.") : root;
    private readonly string _prefix = PrefixPattern().IsMatch(prefix) ? prefix : throw new ArgumentException("The prefix must be 2 to 20 lower-case letters.", nameof(prefix));

    public async Task<StoredFile> SaveAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var now = clock.UtcNow;
        var storageKey = $"{_prefix}/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}";
        var path = PathOf(storageKey);
        var temporaryPath = path + TemporarySuffix;

        Write(() => Directory.CreateDirectory(Path.GetDirectoryName(path)!));

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;

            await using (var target = Write(() => new FileStream(temporaryPath, new FileStreamOptions
                         {
                             Mode = FileMode.CreateNew,
                             Access = FileAccess.Write,
                             Share = FileShare.None,
                             Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                             BufferSize = 0,
                         })))
            {
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
        try
        {
            Stream stream = new FileStream(PathOf(storageKey), new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
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
            throw new StorageUnavailableException("The evidence folder can't be read.", exception);
        }
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(PathOf(storageKey));
        return Task.CompletedTask;
    }

    /// <summary>Only keys this store makes are accepted, and the path must stay inside the root.</summary>
    public string PathOf(string storageKey)
    {
        if (!KeyPattern().IsMatch(storageKey) || !storageKey.StartsWith(_prefix + "/", StringComparison.Ordinal))
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

    [GeneratedRegex("^[a-z]{2,20}/[0-9]{4}/[0-9]{2}/[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[a-z]{2,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    private static T Write<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StorageUnavailableException("The evidence folder can't be written.", exception);
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
            throw new StorageUnavailableException("The evidence folder can't be written.", exception);
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
            // A leftover ".partial" file is never read: it has no row and no valid key.
        }
    }
}

/// <summary>
/// The file types evidence accepts, decided from the first bytes (never the client's name or content type): camera
/// photos (JPEG, PNG, WebP, HEIC) and PDF.
/// </summary>
public static class PrivateFileTypes
{
    public const int HeaderLength = 64;

    public sealed record Kind(string Name, string ContentType, string Extension, bool IsImage);

    public static readonly Kind Jpeg = new("JPEG", "image/jpeg", ".jpg", true);
    public static readonly Kind Png = new("PNG", "image/png", ".png", true);
    public static readonly Kind WebP = new("WebP", "image/webp", ".webp", true);
    public static readonly Kind Heic = new("HEIC", "image/heic", ".heic", true);
    public static readonly Kind Pdf = new("PDF", "application/pdf", ".pdf", false);

    private static readonly string[] HeicBrands = ["heic", "heix", "hevc", "hevx", "heim", "heis", "hevm", "hevs", "mif1", "msf1"];

    public static Kind? Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return Jpeg;
        }

        if (header.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Png;
        }

        if (header.Length >= 12 && header.StartsWith("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return WebP;
        }

        if (header.StartsWith("%PDF-"u8))
        {
            return Pdf;
        }

        if (header.Length >= 12 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = System.Text.Encoding.ASCII.GetString(header.Slice(8, 4));
            if (HeicBrands.Contains(brand, StringComparer.Ordinal))
            {
                return Heic;
            }
        }

        return null;
    }
}

/// <summary>
/// Where a module's private folder is: its own setting when set, otherwise a sub-folder of the Archive's private
/// folder (<c>Archive:Storage:RootPath</c>, already backed up and outside the application folder on servers), otherwise,
/// outside Production, a default folder under the content root. When the folder follows the Archive's, the Archive
/// module checks it at startup and reports it when missing or unwritable, so the module neither repeats nor masks
/// that message; the sub-folder is created on the first upload.
/// </summary>
public static class PrivateFolder
{
    public const string ArchiveRootSetting = "Archive:Storage:RootPath";

    /// <param name="FollowsArchive">True when nothing was set for the module: the Archive module's check covers the folder.</param>
    public sealed record Resolution(string? RootPath, bool FollowsArchive);

    public static Resolution Resolve(string? ownSetting, string archiveSubFolder, string defaultPath, string contentRootPath, bool isProduction, string? archiveRoot)
    {
        if (!string.IsNullOrWhiteSpace(ownSetting))
        {
            return new Resolution(Path.GetFullPath(ownSetting.Trim(), contentRootPath), false);
        }

        if (!string.IsNullOrWhiteSpace(archiveRoot))
        {
            return new Resolution(Path.Combine(Path.GetFullPath(archiveRoot.Trim(), contentRootPath), archiveSubFolder), true);
        }

        return isProduction ? new Resolution(null, true) : new Resolution(Path.GetFullPath(defaultPath, contentRootPath), false);
    }

    /// <summary>The startup check of a folder the module set itself: it must be creatable and writable. Returns the failure, or <c>null</c>.</summary>
    public static string? Check(string settingName, Resolution resolution)
    {
        if (resolution.FollowsArchive)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(resolution.RootPath))
        {
            return $"{settingName} is missing. Set it to a private folder outside the application folder (or set Archive__Storage__RootPath: a sub-folder of it is used).";
        }

        try
        {
            Directory.CreateDirectory(resolution.RootPath);
            var probe = Path.Combine(resolution.RootPath, $".startup-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return $"{settingName} '{resolution.RootPath}' can't be created or written: {exception.Message}";
        }
    }
}
