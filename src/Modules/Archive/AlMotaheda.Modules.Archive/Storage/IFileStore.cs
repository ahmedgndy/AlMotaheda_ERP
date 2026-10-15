namespace AlMotaheda.Modules.Archive.Storage;

/// <summary>
/// Where the bytes of the archive live. The database keeps only the metadata and the key returned here.
/// </summary>
/// <remarks>
/// An interface because storage is an external boundary: tests replace it, and a cloud store can replace the
/// local disk later (decision in <c>06-archive.md</c>). Implementations never hold a whole file in memory and
/// never cache file contents.
/// </remarks>
internal interface IFileStore
{
    /// <summary>
    /// Streams <paramref name="content"/> into a new file of the project, computing its size and SHA-256 on the way.
    /// The file only appears under its key once it is complete.
    /// </summary>
    /// <param name="maxBytes">More bytes than this stops the save, removes what was written, and throws <see cref="FileTooLargeException"/>.</param>
    /// <exception cref="FileTooLargeException">The content is longer than <paramref name="maxBytes"/>.</exception>
    /// <exception cref="StorageUnavailableException">The store can't be written.</exception>
    Task<StoredFile> SaveAsync(int projectId, Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>A seekable read-only stream of the file (so range requests work), or <c>null</c> when it isn't there.</summary>
    /// <exception cref="StorageUnavailableException">The store can't be read.</exception>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Removes the file. Nothing happens when it is already gone.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Writes and removes a tiny file, for the <c>archive-storage</c> health check.</summary>
    /// <exception cref="StorageUnavailableException">The store can't be written.</exception>
    Task ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>A file the store saved: where it is, how big it is, and its hash (lower-case hex).</summary>
internal sealed record StoredFile(string StorageKey, long SizeBytes, string Sha256);

/// <summary>The content was longer than the limit; nothing of it was kept.</summary>
internal sealed class FileTooLargeException(long maxBytes)
    : Exception($"The file is larger than the limit of {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}

/// <summary>The file store can't be read or written (disk full, no permission, folder missing).</summary>
internal sealed class StorageUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
