using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Fleet.Common;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Fleet.Storage;

/// <summary>Where vehicle evidence files (odometer and invoice photos, tickets) are kept. The database keeps metadata and the key.</summary>
/// <remarks>An interface because storage is an external boundary: tests replace it, a cloud store can replace the disk.</remarks>
internal interface IFleetFileStore
{
    /// <summary>Streams the content into a new file, computing its size and SHA-256. The file appears only once complete.</summary>
    /// <exception cref="FleetFileTooLargeException">The content is longer than <paramref name="maxBytes"/>.</exception>
    /// <exception cref="FleetStorageUnavailableException">The store can't be written.</exception>
    Task<FleetStoredFile> SaveAsync(Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>A read-only stream of the file, or <c>null</c> when it isn't there.</summary>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Removes a file that was stored but never recorded (a failed save). Nothing happens when it is gone.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}

internal sealed record FleetStoredFile(string StorageKey, long SizeBytes, string Sha256);

internal sealed class FleetFileTooLargeException(long maxBytes) : Exception($"The file is larger than the limit of {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}

internal sealed class FleetStorageUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Keeps vehicle evidence in a private folder (<c>Fleet:Storage:RootPath</c>) as <c>fleet/{yyyy}/{MM}/{guid}</c>
/// with no extension, so nothing in the folder can be run or served by type. Files are only ever read through the
/// API, after a permission check. The same pattern as the Archive and Office stores.
/// </summary>
internal sealed partial class LocalFleetFileStore(IOptions<FleetStorageOptions> options, CompanyClock clock) : IFleetFileStore
{
    private const int BufferSize = 81_920;
    private const string TemporarySuffix = ".partial";

    private readonly string _root = options.Value.RootPath ?? throw new InvalidOperationException("Fleet:Storage:RootPath is not configured.");

    public async Task<FleetStoredFile> SaveAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var now = clock.UtcNow;
        var storageKey = $"fleet/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}";
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
                        throw new FleetFileTooLargeException(maxBytes);
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

            return new FleetStoredFile(storageKey, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
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
            throw new FleetStorageUnavailableException("The vehicle evidence folder can't be read.", exception);
        }
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(PathOf(storageKey));
        return Task.CompletedTask;
    }

    /// <summary>Only keys this class makes are accepted, and the path must stay inside the root.</summary>
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

    [GeneratedRegex("^fleet/[0-9]{4}/[0-9]{2}/[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    private static T Write<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FleetStorageUnavailableException("The vehicle evidence folder can't be written.", exception);
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
            throw new FleetStorageUnavailableException("The vehicle evidence folder can't be written.", exception);
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
/// The file types vehicle evidence accepts, decided from the first bytes (never the client's name or content type):
/// camera photos (JPEG, PNG, WebP, HEIC) for the odometer, the invoice and a ticket, plus PDF for a printed invoice.
/// </summary>
internal static class FleetFileTypes
{
    public const int HeaderLength = 64;

    internal sealed record Kind(string Name, string ContentType, string Extension, bool IsImage);

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

    /// <summary>Photos are accepted for every purpose; a PDF only for an invoice or a supporting paper (an odometer is always a photo).</summary>
    public static bool Allows(Data.FleetAttachmentPurpose purpose, Kind kind) =>
        kind.IsImage || purpose is Data.FleetAttachmentPurpose.Invoice or Data.FleetAttachmentPurpose.Supporting;
}
