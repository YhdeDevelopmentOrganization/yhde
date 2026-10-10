using Microsoft.Extensions.Logging.Abstractions;
using YHDE.Server.Assets;

namespace YHDE.Server.Tests.Assets;

// A blob store in a throwaway directory.
public sealed class TempBlobStore : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "yhde-blobs-" + Guid.NewGuid().ToString("N"));
    public BlobStore Store { get; }

    public TempBlobStore(long maxBlobBytes = BlobStoreOptions.DefaultMaxBlobBytes, long minFreeBytes = 0)
    {
        Store = new BlobStore(new BlobStoreOptions(Root, maxBlobBytes, minFreeBytes), NullLogger<BlobStore>.Instance);
    }

    public static string HashOf(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    public async Task<string> PutAsync(byte[] bytes)
    {
        var hash = HashOf(bytes);
        await Store.AppendAsync(hash, 0, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        return hash;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
