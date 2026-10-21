using AlMotaheda.BuildingBlocks.Storage;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.HR.Common;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.HR.Storage;

/// <summary>HR request evidence in the shared private store (<see cref="LocalPrivateFileStore"/>), under the <c>hr/</c> prefix.</summary>
internal interface IHrFileStore : IPrivateFileStore;

internal sealed class LocalHrFileStore(IOptions<HrStorageOptions> options, CompanyClock clock) : IHrFileStore
{
    private readonly LocalPrivateFileStore _store = new(options.Value.RootPath ?? throw new InvalidOperationException("Hr:Storage:RootPath is not configured."), "hr", clock);

    public Task<StoredFile> SaveAsync(Stream content, long maxBytes, CancellationToken cancellationToken) => _store.SaveAsync(content, maxBytes, cancellationToken);

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken) => _store.OpenReadAsync(storageKey, cancellationToken);

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken) => _store.DeleteAsync(storageKey, cancellationToken);
}
