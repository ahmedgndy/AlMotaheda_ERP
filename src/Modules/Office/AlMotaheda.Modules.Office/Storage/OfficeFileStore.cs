using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Office.Common;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Office.Storage;

/// <summary>Where office files (receipts, repair photos, quotations) are kept. The database keeps metadata and the key.</summary>
/// <remarks>An interface because storage is an external boundary: tests replace it, a cloud store can replace the disk.</remarks>
internal interface IOfficeFileStore
{
    /// <summary>Streams the content into a new file, computing its size and SHA-256. The file appears only once complete.</summary>
    /// <exception cref="OfficeFileTooLargeException">The content is longer than <paramref name="maxBytes"/>.</exception>
    /// <exception cref="OfficeStorageUnavailableException">The store can't be written.</exception>
    Task<OfficeStoredFile> SaveAsync(Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>A read-only stream of the file, or <c>null</c> when it isn't there.</summary>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Removes a file that was stored but never recorded (a failed save). Nothing happens when it is gone.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}

internal sealed record OfficeStoredFile(string StorageKey, long SizeBytes, string Sha256);

internal sealed class OfficeFileTooLargeException(long maxBytes) : Exception($"The file is larger than the limit of {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}

internal sealed class OfficeStorageUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Keeps office files in a private folder (<c>Office:Storage:RootPath</c>) as <c>office/{yyyy}/{MM}/{guid}</c> with no
/// extension, so nothing in the folder can be run or served by type. Files are only ever read through the API,
/// after a permission check (<c>GET /api/office/attachments/{id}/content</c>).
/// </summary>
internal sealed partial class LocalOfficeFileStore(IOptions<OfficeStorageOptions> options, CompanyClock clock) : IOfficeFileStore
{
    private const int BufferSize = 81_920;
    private const string TemporarySuffix = ".partial";

    private readonly string _root = options.Value.RootPath
        ?? throw new InvalidOperationException("Office:Storage:RootPath is not configured.");

    public async Task<OfficeStoredFile> SaveAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var now = clock.UtcNow;
        var storageKey = $"office/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}";
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
                        throw new OfficeFileTooLargeException(maxBytes);
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

            return new OfficeStoredFile(storageKey, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
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
            throw new OfficeStorageUnavailableException("The office file folder can't be read.", exception);
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

    [GeneratedRegex("^office/[0-9]{4}/[0-9]{2}/[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    private static T Write<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new OfficeStorageUnavailableException("The office file folder can't be written.", exception);
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
            throw new OfficeStorageUnavailableException("The office file folder can't be written.", exception);
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
/// The file types office attachments accept, decided from the first bytes (never the client's name or content type):
/// camera photos (JPEG, PNG, WebP, HEIC) for receipts, repair photos and cash counts, plus PDF for a supplier's
/// quotation or a bank transfer slip.
/// </summary>
internal static class OfficeFileTypes
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

    /// <summary>Photos are accepted for every purpose; a PDF only for a quotation, a transfer slip or a supporting paper.</summary>
    public static bool Allows(Data.OfficeAttachmentPurpose purpose, Kind kind) =>
        kind.IsImage || purpose is Data.OfficeAttachmentPurpose.Quotation or Data.OfficeAttachmentPurpose.Transfer or Data.OfficeAttachmentPurpose.Supporting;
}
