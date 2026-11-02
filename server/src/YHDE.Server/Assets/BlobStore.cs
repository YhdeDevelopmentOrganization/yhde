using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;

namespace YHDE.Server.Assets;

public sealed record BlobStoreOptions(string Root, long MaxBlobBytes, long MinFreeBytes = BlobStoreOptions.DefaultMinFreeBytes, bool Compress = true)
{
    public const long DefaultMaxBlobBytes = 2L * 1024 * 1024 * 1024;
    // Uploads stop while the disk has less than this free, so the database
    // and the server keep room to work.
    public const long DefaultMinFreeBytes = 2L * 1024 * 1024 * 1024;

    public static BlobStoreOptions From(IConfiguration configuration, IHostEnvironment environment)
    {
        var root = configuration["Yhde:BlobPath"];
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(environment.ContentRootPath, "data", "blobs");
        return new BlobStoreOptions(Path.GetFullPath(root), configuration.GetValue("Yhde:MaxAssetBytes", DefaultMaxBlobBytes),
            configuration.GetValue("Yhde:MinFreeDiskBytes", DefaultMinFreeBytes), configuration.GetValue("Yhde:CompressBlobs", true));
    }
}

// Content-addressed store for asset bytes (assets.md). A blob's name is the
// lowercase hex SHA-256 of its bytes, so identical files are stored once and a
// stored blob never changes. Uploads are resumable: bytes are appended to a
// part file at the offset the client names, and the part only becomes a blob
// once its hash matches the name it was uploaded under.
//
// Stored files are compressed on disk where that saves space (Compact, run by
// BlobJanitor): `<hash>.br` holds "YHZ1", the original length (8 bytes,
// little-endian) and the Brotli-compressed bytes. Readers always get the
// original bytes, so hashes, downloads and the log are unchanged. Files that
// are compressed already (PNG, JPEG, Ogg, zip...) stay as they are.
public sealed class BlobStore
{
    private const string CompressedSuffix = ".br";
    private static readonly byte[] Magic = "YHZ1"u8.ToArray();
    private const int HeaderLength = 12;
    // Kept compressed only when it saves at least this share of the size.
    private const double MinSaving = 0.10;
    private const long MinCompressBytes = 8 * 1024;
    private static readonly TimeSpan CompressAfter = TimeSpan.FromMinutes(10);

    public const int HashLength = 64;
    // Unfinished uploads one uploader may have at once.
    public const int MaxUnfinishedParts = 64;
    private static readonly TimeSpan PartLifetime = TimeSpan.FromDays(7);

    private readonly string _blobs;
    private readonly string _uploads;
    private readonly string _skipList;
    private readonly bool _compress;
    private readonly HashSet<string> _incompressible = new(StringComparer.Ordinal);
    private readonly Lock _skipGate = new();
    private readonly ILogger<BlobStore> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public long MaxBlobBytes { get; }
    public long MinFreeBytes { get; }

    public BlobStore(BlobStoreOptions options, ILogger<BlobStore> logger)
    {
        _blobs = Path.Combine(options.Root, "sha256");
        _uploads = Path.Combine(options.Root, "uploads");
        _skipList = Path.Combine(options.Root, "incompressible.txt");
        _compress = options.Compress;
        MaxBlobBytes = options.MaxBlobBytes;
        MinFreeBytes = options.MinFreeBytes;
        _logger = logger;
        Directory.CreateDirectory(_blobs);
        Directory.CreateDirectory(_uploads);
        RemoveStaleParts();
        try
        {
            if (File.Exists(_skipList))
                foreach (var line in File.ReadLines(_skipList))
                    if (IsValidHash(line)) _incompressible.Add(line);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read {List}", _skipList);
        }
    }

    public static bool IsValidHash(string? hash) =>
        hash is { Length: HashLength } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public bool Exists(string hash) => File.Exists(PathOf(hash)) || File.Exists(CompressedPathOf(hash));

    // The file holding a blob's bytes as stored (plain or compressed), or null.
    private string? StoredPath(string hash)
    {
        var plain = PathOf(hash);
        if (File.Exists(plain)) return plain;
        var packed = CompressedPathOf(hash);
        return File.Exists(packed) ? packed : null;
    }

    private string CompressedPathOf(string hash) => PathOf(hash) + CompressedSuffix;

    // The blob name of a stored file ("<hash>" or "<hash>.br"), or null.
    private static string? HashOfFile(string name)
    {
        var hash = name.EndsWith(CompressedSuffix, StringComparison.Ordinal) ? name[..^CompressedSuffix.Length] : name;
        return IsValidHash(hash) ? hash : null;
    }

    // Stored files and the disk space they take (admin page). Walks the
    // store, so the caller caches it.
    public (long Blobs, long Bytes) Usage()
    {
        long count = 0, bytes = 0;
        foreach (var file in new DirectoryInfo(_blobs).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (HashOfFile(file.Name) is null) continue;
            count++;
            bytes += file.Length;
        }
        return (count, bytes);
    }

    // The blob's real size (not its size on disk), or null when not stored.
    public long? SizeOf(string hash)
    {
        var info = new FileInfo(PathOf(hash));
        if (info.Exists) return info.Length;
        try
        {
            using var packed = new FileStream(CompressedPathOf(hash), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadHeader(packed);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return File.Exists(PathOf(hash)) ? new FileInfo(PathOf(hash)).Length : null; // decompressed meanwhile
        }
    }

    private static long ReadHeader(Stream packed)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        packed.ReadExactly(header);
        if (!header[..4].SequenceEqual(Magic)) throw new InvalidDataException("Not a compressed blob.");
        return BinaryPrimitives.ReadInt64LittleEndian(header[4..]);
    }

    // How much of this uploader's interrupted upload the store already holds.
    public long PartialLength(string hash, string uploader = "")
    {
        var info = new FileInfo(PartOf(hash, uploader));
        return info.Exists ? info.Length : 0;
    }

    // Free space on the store's disk; null when it cannot be read.
    public long? FreeBytes()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(_blobs)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Whether `incoming` more bytes would leave less than MinFreeBytes free.
    public bool LowOnDisk(long incoming) => FreeBytes() is { } free && free - incoming < MinFreeBytes;

    public int UnfinishedParts(string uploader) =>
        Directory.EnumerateFiles(_uploads, "*." + SafeUploader(uploader) + ".part").Count();

    // A stored file was asked for again: it is in use, so the clean-up after a
    // project is deleted must not take it now (it keeps files touched within a day).
    public void Touch(string hash)
    {
        try
        {
            if (StoredPath(hash) is { } path) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not touch blob {Hash}", hash);
        }
    }

    // Removes one stored file (a pending upload nobody used). Kept when it
    // changed within a day: someone may be uploading it again right now.
    public bool RemoveIfStale(string hash)
    {
        try
        {
            if (StoredPath(hash) is not { } path || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(1)) return false;
            File.Delete(path);
            File.Delete(PathOf(hash));
            File.Delete(CompressedPathOf(hash));
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not remove unused blob {Hash}", hash);
            return false;
        }
    }

    // The blob's original bytes. The stream knows its length and can seek
    // (forward cheaply), so HTTP range requests keep working.
    public Stream OpenRead(string hash)
    {
        try
        {
            return new FileStream(PathOf(hash), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, useAsync: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            try
            {
                return new PackedReadStream(CompressedPathOf(hash));
            }
            catch (Exception ex2) when (ex2 is FileNotFoundException or DirectoryNotFoundException)
            {
                // Compacted or decompressed between the two checks.
                return new FileStream(PathOf(hash), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, useAsync: true);
            }
        }
    }

    public string PathOf(string hash) => Path.Combine(_blobs, hash[..2], hash[2..4], hash);

    // Each uploader has their own part, so wrong bytes sent by one never reset
    // another's progress (they meet only when the file is complete).
    private string PartOf(string hash, string uploader) => Path.Combine(_uploads, $"{hash}.{SafeUploader(uploader)}.part");

    private static string SafeUploader(string uploader)
    {
        var s = new string(uploader.Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').Take(32).ToArray());
        return s.Length > 0 ? s : "anon";
    }

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

    // Appends `body` to `uploader`'s upload of `hash` at `offset`; `total` is
    // the size of the whole blob. The last chunk completes the blob after
    // verification. With `proveBytes` the bytes are taken even when the store
    // already has the file: a project only gets a file it did not have by
    // showing the whole of it, never by knowing its hash.
    public async Task<AppendResult> AppendAsync(string hash, long offset, long total, Stream body, CancellationToken ct,
        string uploader = "", bool proveBytes = false)
    {
        if (!proveBytes && Exists(hash))
        {
            Touch(hash);
            return new(AppendStatus.AlreadyStored, total);
        }
        if (total < 0 || total > MaxBlobBytes) return new(AppendStatus.TooLarge, 0);

        var key = hash + "." + SafeUploader(uploader);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(30), ct)) return new(AppendStatus.Busy, PartialLength(hash, uploader));
        try
        {
            if (!proveBytes && Exists(hash))
            {
                Touch(hash);
                return new(AppendStatus.AlreadyStored, total);
            }
            var part = PartOf(hash, uploader);
            var current = PartialLength(hash, uploader);
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

            var stored = PartialLength(hash, uploader);
            if (stored < total) return new(AppendStatus.Accepted, stored);

            var actual = await HashFileAsync(part, ct);
            if (!string.Equals(actual, hash, StringComparison.Ordinal))
            {
                File.Delete(part);
                _logger.LogWarning("Upload for {Hash} hashed to {Actual}; discarded", hash, actual);
                return new(AppendStatus.HashMismatch, 0);
            }
            var destination = PathOf(hash);
            if (Exists(hash))
            {
                // Proven, and the store had it already: keep the one copy.
                File.Delete(part);
                Touch(hash);
                return new(AppendStatus.Completed, total);
            }
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
            if (HashOfFile(file.Name) is not { } hash) continue; // a temporary file of Compact
            if (referenced.Contains(hash) || DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromDays(1)) continue;
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

    // Unfinished uploads nobody continued for a week (run at start and by
    // BlobJanitor every hour).
    public void RemoveStaleParts()
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

    // Compresses stored files that were not touched for a while and are worth
    // it, until `budget` runs out. Each result is decompressed and hash
    // checked before the plain file goes. Returns how many files and bytes it
    // saved.
    public async Task<(int Files, long Saved)> CompactAsync(TimeSpan budget, CancellationToken ct)
    {
        if (!_compress) return (0, 0);
        var until = DateTime.UtcNow + budget;
        int files = 0;
        long saved = 0;
        foreach (var file in new DirectoryInfo(_blobs).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > until) break;
            if (!IsValidHash(file.Name)) continue; // compressed already, or not a blob
            var hash = file.Name;
            lock (_skipGate)
                if (_incompressible.Contains(hash)) continue;
            if (file.Length < MinCompressBytes || DateTime.UtcNow - file.LastWriteTimeUtc < CompressAfter) continue;
            try
            {
                var gain = await CompressOneAsync(file, hash, ct);
                if (gain > 0)
                {
                    files++;
                    saved += gain;
                }
                else
                {
                    MarkIncompressible(hash);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not compress blob {Hash}", hash);
            }
        }
        if (files > 0) _logger.LogInformation("Compressed {Files} stored files, saving {Bytes} bytes", files, saved);
        return (files, saved);
    }

    private void MarkIncompressible(string hash)
    {
        lock (_skipGate)
        {
            if (!_incompressible.Add(hash)) return;
            try
            {
                File.AppendAllText(_skipList, hash + "\n");
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not write {List}", _skipList);
            }
        }
    }

    // Formats that are compressed already, by their first bytes.
    internal static bool LooksCompressed(ReadOnlySpan<byte> h) =>
        h.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G'])
        || h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])                                    // JPEG
        || (h.Length >= 12 && h.StartsWith("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8))          // WebP
        || h.StartsWith("OggS"u8) || h.StartsWith("ID3"u8) || h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFB])
        || h.StartsWith("PK"u8) || h.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B])                  // zip, gzip
        || h.StartsWith((ReadOnlySpan<byte>)[0x28, 0xB5, 0x2F, 0xFD]) || h.StartsWith("7z"u8)      // zstd, 7z
        || (h.Length >= 8 && h[4..8].SequenceEqual("ftyp"u8))                                      // mp4, mov
        || h.StartsWith((ReadOnlySpan<byte>)[0xAB, (byte)'K', (byte)'T', (byte)'X', (byte)' ', (byte)'2'])
        || h.StartsWith("GST2"u8) || h.StartsWith("GDST"u8);                                       // Godot .ctex/.stex

    // Bytes saved, or 0 when the file is not worth compressing.
    private async Task<long> CompressOneAsync(FileInfo file, string hash, CancellationToken ct)
    {
        var plain = file.FullName;
        var length = file.Length;
        var head = new byte[16];
        await using (var probe = new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var n = await probe.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
            if (LooksCompressed(head.AsSpan(0, n))) return 0;
            // A quick try on the start before compressing it all.
            probe.Position = 0;
            var sample = new byte[(int)Math.Min(length, 1 << 20)];
            await probe.ReadExactlyAsync(sample, ct);
            using var sampleOut = new MemoryStream();
            await using (var brotli = new BrotliStream(sampleOut, CompressionLevel.Fastest, leaveOpen: true))
                await brotli.WriteAsync(sample, ct);
            if (sampleOut.Length > sample.Length * (1 - MinSaving)) return 0;
        }

        var tmp = CompressedPathOf(hash) + ".tmp";
        try
        {
            await using (var source = new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, useAsync: true))
            await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var header = new byte[HeaderLength];
                Magic.CopyTo(header, 0);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(4), length);
                await output.WriteAsync(header, ct);
                await using (var brotli = new BrotliStream(output, new BrotliCompressionOptions { Quality = 5 }, leaveOpen: true))
                    await source.CopyToAsync(brotli, 1 << 16, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
                if (output.Length > length * (1 - MinSaving))
                {
                    output.Close();
                    File.Delete(tmp);
                    return 0;
                }
            }
            // Never trust the result without reading it back.
            await using (var check = new PackedReadStream(tmp))
            {
                if (check.Length != length || Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct)) != hash)
                    throw new InvalidDataException("The compressed copy did not read back the same.");
            }
            var packedLength = new FileInfo(tmp).Length;
            // The compressed copy is in place before the plain one goes, so a
            // reader always finds one of them.
            File.Move(tmp, CompressedPathOf(hash), overwrite: true);
            File.Delete(plain);
            return length - packedLength;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    // Reads a `.br` blob as its original bytes. Seeking forward decompresses
    // and skips; seeking back starts again from the beginning.
    private sealed class PackedReadStream : Stream
    {
        private readonly string _path;
        private FileStream _file;
        private BrotliStream _brotli;
        private long _position;

        public PackedReadStream(string path)
        {
            _path = path;
            (_file, _brotli, Length) = Open(path);
        }

        private static (FileStream, BrotliStream, long) Open(string path)
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, useAsync: true);
            try
            {
                var length = ReadHeader(file);
                return (file, new BrotliStream(file, CompressionMode.Decompress, leaveOpen: true), length);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public override long Length { get; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = _brotli.Read(buffer);
            _position += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await _brotli.ReadAsync(buffer, ct);
            _position += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => Length + offset,
            };
            if (target < 0 || target > Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (target < _position)
            {
                _brotli.Dispose();
                _file.Dispose();
                (_file, _brotli, _) = Open(_path);
                _position = 0;
            }
            var skip = ArrayPool<byte>.Shared.Rent(1 << 16);
            try
            {
                while (_position < target)
                {
                    var n = Read(skip.AsSpan(0, (int)Math.Min(skip.Length, target - _position)));
                    if (n == 0) throw new EndOfStreamException();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(skip);
            }
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _brotli.Dispose();
                _file.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _brotli.DisposeAsync();
            await _file.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
