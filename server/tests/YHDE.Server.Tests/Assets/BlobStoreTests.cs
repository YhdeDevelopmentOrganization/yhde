using System.Text;
using FluentAssertions;
using YHDE.Server.Assets;

namespace YHDE.Server.Tests.Assets;

// The content-addressed blob store (assets.md).
public sealed class BlobStoreTests : IDisposable
{
    private readonly TempBlobStore _temp = new(maxBlobBytes: 1024);
    private BlobStore Store => _temp.Store;

    public void Dispose() => _temp.Dispose();

    private static MemoryStream Body(byte[] bytes) => new(bytes);

    [Fact]
    public async Task Stores_a_blob_under_its_hash_in_one_request()
    {
        var bytes = Encoding.UTF8.GetBytes("hello asset");
        var hash = TempBlobStore.HashOf(bytes);

        var result = await Store.AppendAsync(hash, 0, bytes.Length, Body(bytes), CancellationToken.None);

        result.Status.Should().Be(BlobStore.AppendStatus.Completed);
        Store.Exists(hash).Should().BeTrue();
        Store.SizeOf(hash).Should().Be(bytes.Length);
        (await File.ReadAllBytesAsync(Store.PathOf(hash))).Should().Equal(bytes);
    }

    [Fact]
    public async Task Resumes_an_interrupted_upload_from_the_stored_offset()
    {
        var bytes = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var hash = TempBlobStore.HashOf(bytes);

        (await Store.AppendAsync(hash, 0, bytes.Length, Body(bytes[..100]), CancellationToken.None))
            .Should().Be(new BlobStore.AppendResult(BlobStore.AppendStatus.Accepted, 100));
        Store.PartialLength(hash).Should().Be(100);
        Store.Exists(hash).Should().BeFalse();

        // A client that lost track of the offset is told where to resume.
        (await Store.AppendAsync(hash, 0, bytes.Length, Body(bytes), CancellationToken.None))
            .Should().Be(new BlobStore.AppendResult(BlobStore.AppendStatus.OffsetMismatch, 100));

        (await Store.AppendAsync(hash, 100, bytes.Length, Body(bytes[100..]), CancellationToken.None))
            .Status.Should().Be(BlobStore.AppendStatus.Completed);
        (await File.ReadAllBytesAsync(Store.PathOf(hash))).Should().Equal(bytes);
        Store.PartialLength(hash).Should().Be(0);
    }

    [Fact]
    public async Task Refuses_bytes_that_do_not_match_their_name()
    {
        var hash = TempBlobStore.HashOf(Encoding.UTF8.GetBytes("expected"));
        var other = Encoding.UTF8.GetBytes("tampered");

        var result = await Store.AppendAsync(hash, 0, other.Length, Body(other), CancellationToken.None);

        result.Status.Should().Be(BlobStore.AppendStatus.HashMismatch);
        Store.Exists(hash).Should().BeFalse();
        Store.PartialLength(hash).Should().Be(0);
    }

    [Fact]
    public async Task Identical_bytes_are_stored_once()
    {
        var bytes = Encoding.UTF8.GetBytes("same");
        var hash = await _temp.PutAsync(bytes);

        var again = await Store.AppendAsync(hash, 0, bytes.Length, Body(bytes), CancellationToken.None);

        again.Status.Should().Be(BlobStore.AppendStatus.AlreadyStored);
    }

    [Fact]
    public async Task Enforces_the_size_limit_and_the_announced_length()
    {
        var big = new byte[2048];
        (await Store.AppendAsync(TempBlobStore.HashOf(big), 0, big.Length, Body(big), CancellationToken.None))
            .Status.Should().Be(BlobStore.AppendStatus.TooLarge);

        var bytes = new byte[64];
        var hash = TempBlobStore.HashOf(bytes);
        (await Store.AppendAsync(hash, 0, 32, Body(bytes), CancellationToken.None))
            .Status.Should().Be(BlobStore.AppendStatus.TooLarge);
        Store.PartialLength(hash).Should().Be(0);
    }

    [Theory]
    [InlineData("abc", false)]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", false)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", true)]
    [InlineData("../../../../etc/passwd/abcdef0123456789abcdef0123456789abcdef0", false)]
    public void Only_lowercase_sha256_names_are_accepted(string hash, bool valid) =>
        BlobStore.IsValidHash(hash).Should().Be(valid);
}
