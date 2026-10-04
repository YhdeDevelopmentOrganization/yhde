using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace YHDE.Server.Assets;

public sealed record BlobStoreOptions(string Root, long MaxBlobBytes)
{
    public const long DefaultMaxBlobBytes = 2L * 1024 * 1024 * 1024;

    public static BlobStoreOptions From(IConfiguration configuration, IHostEnvironment environment)
    {
        var root = configuration["Yhde:BlobPath"];
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(environment.ContentRootPath, "data", "blobs");
        return new BlobStoreOptions(Path.GetFullPath(root), configuration.GetValue("Yhde:MaxAssetBytes", DefaultMaxBlobBytes));
    }
}

// Content-addressed store for asset bytes (assets.md). A blob's name is the
// lowercase hex SHA-256 of its bytes, so identical files are stored once and a
// stored blob never changes. Uploads are resumable: bytes are appended to a
// part file at the offset the client names, and the part only becomes a blob
// once its hash matches the name it was uploaded under.
public sealed class BlobStore
{
    public const int HashLength = 64;
    private static readonly TimeSpan PartLifetime = TimeSpan.FromDays(7);

    private readonly string _blobs;
    private readonly string _uploads;
    private readonly ILogger<BlobStore> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public long MaxBlobBytes { get; }

    public BlobStore(BlobStoreOptions options, ILogger<BlobStore> logger)
    {
        _blobs = Path.Combine(options.Root, "sha256");
        _uploads = Path.Combine(options.Root, "uploads");
        MaxBlobBytes = options.MaxBlobBytes;
        _logger = logger;
        Directory.CreateDirectory(_blobs);
        Directory.CreateDirectory(_uploads);
        RemoveStaleParts();
    }

    public static bool IsValidHash(string? hash) =>
        hash is { Length: HashLength } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public bool Exists(string hash) => File.Exists(PathOf(hash));

    // Stored files and their total size (admin page). Walks the store, so the
    // caller caches it.
    public (long Blobs, long Bytes) Usage()
    {
        long count = 0, bytes = 0;
        foreach (var file in new DirectoryInfo(_blobs).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            count++;
            bytes += file.Length;
        }
        return (count, bytes);
    }

    public long? SizeOf(string hash)
    {
        var info = new FileInfo(PathOf(hash));
        return info.Exists ? info.Length : null;
    }

    // How much of an interrupted upload the store already holds.
    public long PartialLength(string hash)
    {
        var info = new FileInfo(PartOf(hash));
        return info.Exists ? info.Length : 0;
    }

    public FileStream OpenRead(string hash) =>
        new(PathOf(hash), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);

    public string PathOf(string hash) => Path.Combine(_blobs, hash[..2], hash[2..4], hash);

    private string PartOf(string hash) => Path.Combine(_uploads, hash + ".part");

    public enum AppendStatus
    {
        Accepted,      // stored up to Offset; send the rest
        Completed,     // the blob is now stored
        AlreadyStored, // nothing to do (dedup)
        OffsetMismatch,// resume from Offset instead
        TooLarge,
        HashMismatch,  // the bytes do not hash to the name; the part was discarded
        Busy,          // another upload of the same blob is in progress
    }

    public sealed record AppendResult(AppendStatus Status, long Offset);

    // Appends `body` to the upload of `hash` at `offset`; `total` is the size
    // of the whole blob. The last chunk completes the blob after verification.
    public async Task<AppendResult> AppendAsync(string hash, long offset, long total, Stream body, CancellationToken ct)
    {
        if (Exists(hash)) return new(AppendStatus.AlreadyStored, total);
        if (total < 0 || total > MaxBlobBytes) return new(AppendStatus.TooLarge, 0);

        var gate = _locks.GetOrAdd(hash, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(30), ct)) return new(AppendStatus.Busy, PartialLength(hash));
        try
        {
            if (Exists(hash)) return new(AppendStatus.AlreadyStored, total);
            var part = PartOf(hash);
            var current = PartialLength(hash);
            if (current > total)
            {
                File.Delete(part); // a previous attempt announced another size
                current = 0;
            }
            if (offset != current) return new(AppendStatus.OffsetMismatch, current);

            var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
            try
            {
                await using var file = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var written = current;
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    if (written + read > total)
                    {
                        file.SetLength(current);
                        return new(AppendStatus.TooLarge, current);
                    }
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var stored = PartialLength(hash);
            if (stored < total) return new(AppendStatus.Accepted, stored);

            var actual = await HashFileAsync(part, ct);
            if (!string.Equals(actual, hash, StringComparison.Ordinal))
            {
                File.Delete(part);
                _logger.LogWarning("Upload for {Hash} hashed to {Actual}; discarded", hash, actual);
                return new(AppendStatus.HashMismatch, 0);
            }
            var destination = PathOf(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(part, destination, overwrite: true);
            _logger.LogInformation("Stored blob {Hash} ({Bytes} bytes)", hash, total);
            return new(AppendStatus.Completed, total);
        }
        finally
        {
            gate.Release();
        }
    }

    // A scratch file on the same disk as the store (big admin uploads).
    public string NewTempPath() => Path.Combine(_uploads, Guid.NewGuid().ToString("N") + ".tmp");

    // Stores bytes the server itself has (a game zip on the admin page).
    // Returns the blob's hash and size, or null when it is larger than allowed.
    public async Task<(string Hash, long Size)?> PutAsync(Stream source, CancellationToken ct)
    {
        var tmp = NewTempPath();
        try
        {
            long size;
            string hash;
            await using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        if (file.Length + read > MaxBlobBytes) return null;
                        sha.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                size = file.Length;
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            if (!Exists(hash))
            {
                var destination = PathOf(hash);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(tmp, destination, overwrite: true);
            }
            return (hash, size);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    // Removes stored files no operation refers to any more (after a project
    // was deleted). Files younger than a day are kept: an upload in progress
    // is not yet in the log.
    public (int Files, long Bytes) RemoveUnreferenced(IReadOnlySet<string> referenced)
    {
        int files = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(_blobs).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (referenced.Contains(file.Name) || DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromDays(1)) continue;
            try
            {
                var length = file.Length;
                file.Delete();
                files++;
                bytes += length;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not remove unused blob {Blob}", file.Name);
            }
        }
        return (files, bytes);
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var digest = await SHA256.HashDataAsync(file, ct);
        return Convert.ToHexStringLower(digest);
    }

    private void RemoveStaleParts()
    {
        foreach (var part in Directory.EnumerateFiles(_uploads, "*.part"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(part) > PartLifetime) File.Delete(part);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not remove stale upload {Part}", part);
            }
        }
    }
}
