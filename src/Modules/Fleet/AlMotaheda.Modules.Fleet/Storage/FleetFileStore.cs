using AlMotaheda.BuildingBlocks.Storage;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Fleet.Common;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Fleet.Storage;

/// <summary>
/// Where vehicle evidence files (odometer and invoice photos, tickets) are kept: the shared private store
/// (<see cref="LocalPrivateFileStore"/>) in the Fleet folder (<c>Fleet:Storage:RootPath</c>) under the <c>fleet/</c>
/// prefix. The database keeps metadata and the key; files are only ever read through the API after a permission check.
/// </summary>
internal interface IFleetFileStore : IPrivateFileStore;

internal sealed class LocalFleetFileStore(IOptions<FleetStorageOptions> options, CompanyClock clock) : IFleetFileStore
{
    private readonly LocalPrivateFileStore _store = new(options.Value.RootPath ?? throw new InvalidOperationException("Fleet:Storage:RootPath is not configured."), "fleet", clock);

    public Task<StoredFile> SaveAsync(Stream content, long maxBytes, CancellationToken cancellationToken) => _store.SaveAsync(content, maxBytes, cancellationToken);

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken) => _store.OpenReadAsync(storageKey, cancellationToken);

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken) => _store.DeleteAsync(storageKey, cancellationToken);

    internal string PathOf(string storageKey) => _store.PathOf(storageKey);
}

/// <summary>
/// The file types vehicle evidence accepts (<see cref="PrivateFileTypes"/>, decided from the bytes): camera photos
/// for the odometer, the invoice and a ticket, plus PDF for a printed invoice.
/// </summary>
internal static class FleetFileTypes
{
    public const int HeaderLength = PrivateFileTypes.HeaderLength;

    public static PrivateFileTypes.Kind Jpeg => PrivateFileTypes.Jpeg;
    public static PrivateFileTypes.Kind Png => PrivateFileTypes.Png;
    public static PrivateFileTypes.Kind WebP => PrivateFileTypes.WebP;
    public static PrivateFileTypes.Kind Heic => PrivateFileTypes.Heic;
    public static PrivateFileTypes.Kind Pdf => PrivateFileTypes.Pdf;

    public static PrivateFileTypes.Kind? Detect(ReadOnlySpan<byte> header) => PrivateFileTypes.Detect(header);

    /// <summary>Photos are accepted for every purpose; a PDF only for an invoice or a supporting paper (an odometer is always a photo).</summary>
    public static bool Allows(Data.FleetAttachmentPurpose purpose, PrivateFileTypes.Kind kind) =>
        kind.IsImage || purpose is Data.FleetAttachmentPurpose.Invoice or Data.FleetAttachmentPurpose.Supporting;
}
