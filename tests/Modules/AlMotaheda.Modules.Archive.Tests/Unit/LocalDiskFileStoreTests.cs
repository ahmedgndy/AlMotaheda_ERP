using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Archive.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Archive.Tests.Unit;

/// <summary>The local disk store against a temporary folder: save, open, delete, hash, size, and limits.</summary>
public sealed class LocalDiskFileStoreTests : IDisposable
{
    private readonly TemporaryFolder _folder = new();

    [Fact]
    public async Task Save_streams_the_file_and_reports_its_size_and_sha256()
    {
        var store = CreateStore();
        var bytes = RandomNumberGenerator.GetBytes(300_000);

        var stored = await store.SaveAsync(7, new MemoryStream(bytes), maxBytes: 1_000_000, CancellationToken.None);

        Assert.Equal(300_000, stored.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), stored.Sha256);
        Assert.Matches(new Regex($"^7/{DateTime.UtcNow:yyyy}/{DateTime.UtcNow:MM}/[0-9a-f]{{32}}$"), stored.StorageKey);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.PathOf(stored.StorageKey)));
        Assert.Single(_folder.Files());
    }

    [Fact]
    public async Task Saved_files_have_no_extension_and_no_temporary_file_is_left()
    {
        var store = CreateStore();

        var stored = await store.SaveAsync(7, new MemoryStream(SampleFiles.Pdf()), 10_000, CancellationToken.None);

        var file = Assert.Single(_folder.Files());
        Assert.Equal("", Path.GetExtension(file));
        Assert.Equal(store.PathOf(stored.StorageKey), file);
    }

    [Fact]
    public async Task Open_returns_a_seekable_stream_of_the_bytes()
    {
        var store = CreateStore();
        var bytes = SampleFiles.Jpeg(5000);
        var stored = await store.SaveAsync(3, new MemoryStream(bytes), 10_000, CancellationToken.None);

        await using var stream = await store.OpenReadAsync(stored.StorageKey, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.True(stream.CanSeek);
        Assert.Equal(5000, stream.Length);
        var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public async Task Opening_a_file_that_is_not_there_answers_null()
    {
        var store = CreateStore();

        Assert.Null(await store.OpenReadAsync($"3/2026/01/{Guid.NewGuid():N}", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_removes_the_file_and_is_quiet_when_it_is_already_gone()
    {
        var store = CreateStore();
        var stored = await store.SaveAsync(3, new MemoryStream(SampleFiles.Pdf()), 10_000, CancellationToken.None);

        await store.DeleteAsync(stored.StorageKey, CancellationToken.None);
        await store.DeleteAsync(stored.StorageKey, CancellationToken.None);

        Assert.Empty(_folder.Files());
        Assert.Null(await store.OpenReadAsync(stored.StorageKey, CancellationToken.None));
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused_and_nothing_of_it_is_kept()
    {
        var store = CreateStore();

        await Assert.ThrowsAsync<FileTooLargeException>(() =>
            store.SaveAsync(3, new MemoryStream(new byte[200_001]), maxBytes: 200_000, CancellationToken.None));

        Assert.Empty(_folder.Files());
    }

    [Fact]
    public async Task A_file_of_exactly_the_limit_is_accepted()
    {
        var store = CreateStore();

        var stored = await store.SaveAsync(3, new MemoryStream(new byte[200_000]), maxBytes: 200_000, CancellationToken.None);

        Assert.Equal(200_000, stored.SizeBytes);
    }

    [Fact]
    public async Task An_upload_that_breaks_off_leaves_no_partial_file()
    {
        var store = CreateStore();

        // The client drops the connection after 100 KB: the read error passes through unchanged.
        await Assert.ThrowsAsync<IOException>(() =>
            store.SaveAsync(3, new BrokenStream(100_000), maxBytes: 1_000_000, CancellationToken.None));

        Assert.Empty(_folder.Files());
    }

    [Fact]
    public async Task A_cancelled_upload_leaves_no_partial_file()
    {
        var store = CreateStore();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(3, new MemoryStream(new byte[1000]), maxBytes: 1_000_000, cancellation.Token));

        Assert.Empty(_folder.Files());
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("3/2026/01/../../../../etc/passwd")]
    [InlineData("3/2026/01/ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("/3/2026/01/0123456789abcdef0123456789abcdef")]
    [InlineData("0/2026/01/0123456789abcdef0123456789abcdef")]
    [InlineData("")]
    public async Task Keys_the_store_did_not_make_are_refused(string storageKey)
    {
        var store = CreateStore();

        Assert.Throws<ArgumentException>(() => store.PathOf(storageKey));
        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync(storageKey, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(storageKey, CancellationToken.None));
    }

    [Fact]
    public async Task The_probe_writes_and_removes_a_file()
    {
        var store = CreateStore();

        await store.ProbeAsync(CancellationToken.None);

        Assert.Empty(_folder.Files());
    }

    [Fact]
    public async Task A_folder_that_cannot_be_written_is_reported_as_unavailable()
    {
        // The "folder" is a file, so nothing can be created inside it.
        var blocked = Path.Combine(_folder.Path, "not-a-folder");
        await File.WriteAllTextAsync(blocked, "x");
        var store = CreateStore(blocked);

        await Assert.ThrowsAsync<StorageUnavailableException>(() => store.ProbeAsync(CancellationToken.None));
        await Assert.ThrowsAsync<StorageUnavailableException>(() =>
            store.SaveAsync(3, new MemoryStream(SampleFiles.Pdf()), 10_000, CancellationToken.None));
    }

    [Fact]
    public void The_storage_folder_is_created_and_checked_at_startup()
    {
        var validator = new ArchiveStorageOptionsValidator();
        var created = Path.Combine(_folder.Path, "new", "archive");
        var blocked = Path.Combine(_folder.Path, "a-file");
        File.WriteAllText(blocked, "x");

        var good = validator.Validate(null, new ArchiveStorageOptions { RootPath = created });
        var missing = validator.Validate(null, new ArchiveStorageOptions { RootPath = " " });
        var bad = validator.Validate(null, new ArchiveStorageOptions { RootPath = Path.Combine(blocked, "archive") });

        Assert.True(good.Succeeded);
        Assert.True(Directory.Exists(created));
        Assert.Empty(Directory.GetFiles(created));
        Assert.True(missing.Failed);
        Assert.Contains("Archive:Storage:RootPath is missing", missing.FailureMessage);
        Assert.True(bad.Failed);
        Assert.Contains("can't be created or written", bad.FailureMessage);
    }

    public void Dispose() => _folder.Dispose();

    private LocalDiskFileStore CreateStore(string? root = null) =>
        new(
            Options.Create(new ArchiveStorageOptions { RootPath = root ?? _folder.Path }),
            new CompanyClock(TimeProvider.System, new ConfigurationBuilder().Build()));

    /// <summary>Gives some bytes, then fails like a dropped connection.</summary>
    private sealed class BrokenStream(int bytesBeforeFailure) : Stream
    {
        private int _given;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_given >= bytesBeforeFailure)
            {
                throw new IOException("The client disconnected.");
            }

            var giving = Math.Min(count, bytesBeforeFailure - _given);
            _given += giving;
            return giving;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var array = new byte[buffer.Length];
            var read = Read(array, 0, array.Length);
            array.AsMemory(0, read).CopyTo(buffer);
            return ValueTask.FromResult(read);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>A new empty folder under the system's temporary folder, removed at the end of the test.</summary>
internal sealed class TemporaryFolder : IDisposable
{
    public TemporaryFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Every file under the folder, at any depth.</summary>
    public IReadOnlyList<string> Files() => Directory.GetFiles(Path, "*", SearchOption.AllDirectories);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A file still open by a test host that is shutting down; the system cleans its temporary folder.
        }
    }
}
