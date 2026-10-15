using System.Text;
using AlMotaheda.Modules.Archive.Common;
using AlMotaheda.Modules.Archive.Files;
using AlMotaheda.Modules.Archive.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace AlMotaheda.Modules.Archive.Features.Documents;

/// <summary>The file of an upload once it is in the store: where, what type, and the cleaned display name.</summary>
internal sealed record ReceivedFile(StoredFile Stored, FileType Type, string FileName);

/// <summary>Why an upload's file was refused, before any metadata is looked at.</summary>
internal sealed record UploadFailure(string Code, long MaxBytes = 0, string? DetectedType = null);

/// <summary>
/// Reads an upload's <c>multipart/form-data</c> body part by part, straight from the request. The file part is
/// streamed into the file store as it arrives; it is never buffered in memory or written to a temporary file by the
/// framework (which is what binding an <c>IFormFile</c> would do).
/// </summary>
/// <remarks>
/// <para>The parts may come in any order. Text fields are short (at most <see cref="MaxFieldLength"/> characters);
/// unknown parts are skipped.</para>
/// <para>The type is decided from the first <see cref="FileTypes.HeaderLength"/> bytes before anything is stored, so
/// a refused type never reaches the disk. A file that is too large for its kind is stopped by the store and removed.
/// Once a file is stored, <see cref="File"/> is set at once, so the caller can remove it whatever happens next.</para>
/// </remarks>
internal sealed class UploadForm
{
    /// <summary>Longer than any title (200) or description (1000); a longer text field is refused, not cut.</summary>
    public const int MaxFieldLength = 4000;

    /// <summary>The form fields the upload reads. Field names are case-insensitive.</summary>
    public static readonly IReadOnlyList<string> FieldNames = ["title", "category", "description", "documentDate", "entityType", "entityId"];

    private const int MaxBoundaryLength = 70;

    private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The stored file, as soon as it is in the store. <c>null</c> while none was received.</summary>
    public ReceivedFile? File { get; private set; }

    /// <summary>Problems with the form fields themselves (too long, sent twice, two files).</summary>
    public ValidationErrors Errors { get; } = new();

    public string? Field(string name) => _fields.GetValueOrDefault(name);

    /// <summary>True when the request is <c>multipart/form-data</c> with a usable boundary.</summary>
    public static bool TryGetBoundary(HttpRequest request, out string boundary)
    {
        boundary = "";
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxBoundaryLength)
        {
            return false;
        }

        boundary = value;
        return true;
    }

    /// <summary>Reads every part. Returns why the file was refused, or <c>null</c> when it was accepted (or not sent).</summary>
    /// <exception cref="StorageUnavailableException">The store can't be written.</exception>
    /// <exception cref="InvalidDataException">The body is not valid multipart data.</exception>
    public async Task<UploadFailure?> ReadAsync(
        HttpRequest request, string boundary, int projectId, IFileStore store, ArchiveLimitsOptions limits, CancellationToken cancellationToken)
    {
        var reader = new MultipartReader(boundary, request.Body);

        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            // IsFormDisposition is true only for parts without a file name, so the file part is checked separately.
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || !(disposition.IsFormDisposition() || disposition.IsFileDisposition()))
            {
                continue;
            }

            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";

            if (disposition.IsFileDisposition())
            {
                if (!name.Equals(ArchiveProblems.FileField, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File is not null)
                {
                    Errors.Add(ArchiveProblems.FileField, "Send one file per document.");
                    continue;
                }

                var failure = await ReceiveFileAsync(section, disposition, projectId, store, limits, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                continue;
            }

            var field = FieldNames.FirstOrDefault(known => known.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                continue;
            }

            var value = await ReadTextAsync(section.Body, cancellationToken);
            if (value is null)
            {
                Errors.Add(field, $"{field} is too long.");
            }
            else if (!_fields.TryAdd(field, value))
            {
                Errors.Add(field, $"Send {field} once.");
            }
        }

        return null;
    }

    private async Task<UploadFailure?> ReceiveFileAsync(
        MultipartSection section,
        ContentDispositionHeaderValue disposition,
        int projectId,
        IFileStore store,
        ArchiveLimitsOptions limits,
        CancellationToken cancellationToken)
    {
        // filename* (RFC 5987) carries non-ASCII names such as Arabic ones; plain filename is the fallback.
        var sentName = disposition.FileNameStar.HasValue
            ? disposition.FileNameStar.Value
            : HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
        var fileName = FileNames.Clean(sentName);

        var header = new byte[FileTypes.HeaderLength];
        var headerLength = await section.Body.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        if (headerLength == 0)
        {
            return new UploadFailure(ArchiveProblems.FileEmpty);
        }

        var type = FileTypes.Detect(header.AsSpan(0, headerLength));
        if (type is null)
        {
            return new UploadFailure(ArchiveProblems.FileTypeNotAllowed);
        }

        // An office package's real type is known only after its table of contents is read (below).
        if (type != FileTypes.OfficePackage && !FileTypes.ExtensionAgrees(type, fileName))
        {
            return new UploadFailure(ArchiveProblems.FileExtensionMismatch, DetectedType: type.Name);
        }

        var maxBytes = limits.MaxBytesFor(type.Kind);
        StoredFile stored;
        try
        {
            stored = await store.SaveAsync(projectId, new PrefixedStream(header, headerLength, section.Body), maxBytes, cancellationToken);
        }
        catch (FileTooLargeException)
        {
            return new UploadFailure(ArchiveProblems.FileTooLarge, MaxBytes: maxBytes);
        }

        File = new ReceivedFile(stored, type, fileName);

        if (type == FileTypes.OfficePackage)
        {
            await using var package = await store.OpenReadAsync(stored.StorageKey, cancellationToken)
                ?? throw new StorageUnavailableException("The file just stored can't be read back.");

            var office = FileTypes.IdentifyOfficePackage(package);
            if (office is null)
            {
                return new UploadFailure(ArchiveProblems.FileTypeNotAllowed);
            }

            if (!FileTypes.ExtensionAgrees(office, fileName))
            {
                return new UploadFailure(ArchiveProblems.FileExtensionMismatch, DetectedType: office.Name);
            }

            type = office;
        }

        File = new ReceivedFile(stored, type, FileNames.WithExtension(fileName, type));
        return null;
    }

    /// <summary>The text of a field, or <c>null</c> when it is longer than <see cref="MaxFieldLength"/>.</summary>
    private static async Task<string?> ReadTextAsync(Stream body, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var buffer = new char[MaxFieldLength + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await reader.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
        {
            total += read;
        }

        return total > MaxFieldLength ? null : new string(buffer, 0, total);
    }

    /// <summary>The bytes already read to detect the type, then the rest of the part: the store sees the whole file.</summary>
    private sealed class PrefixedStream(byte[] prefix, int prefixLength, Stream rest) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefixLength)
            {
                var count = Math.Min(buffer.Length, prefixLength - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            return await rest.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        // The request body may only be read asynchronously (Kestrel refuses synchronous reads).
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
