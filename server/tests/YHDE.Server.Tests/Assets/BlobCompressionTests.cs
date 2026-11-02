using System.Text;
using FluentAssertions;

namespace YHDE.Server.Tests.Assets;

// Stored files are compressed on disk where it saves space, and read back
// exactly as uploaded (BlobStore.CompactAsync).
public sealed class BlobCompressionTests
{
    private static byte[] Text(int lines) =>
        Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, lines).Select(i => $"[node name=\"Tree{i}\" parent=\".\"]\nposition = Vector2({i}, {i * 3})\n")));

    private static void Age(TempBlobStore temp, string hash) =>
        File.SetLastWriteTimeUtc(temp.Store.PathOf(hash), DateTime.UtcNow.AddHours(-2));

    [Fact]
    public async Task A_text_file_is_compressed_and_reads_back_byte_for_byte()
    {
        using var temp = new TempBlobStore();
        var bytes = Text(20_000);
        var hash = await temp.PutAsync(bytes);
        Age(temp, hash);

        var (files, saved) = await temp.Store.CompactAsync(TimeSpan.FromMinutes(1), default);

        files.Should().Be(1);
        saved.Should().BeGreaterThan(bytes.Length / 2);
        File.Exists(temp.Store.PathOf(hash)).Should().BeFalse("the plain copy goes once the compressed one is checked");
        File.Exists(temp.Store.PathOf(hash) + ".br").Should().BeTrue();
        temp.Store.Exists(hash).Should().BeTrue();
        temp.Store.SizeOf(hash).Should().Be(bytes.Length);
        temp.Store.Usage().Bytes.Should().BeLessThan(bytes.Length / 2);

        await using (var read = temp.Store.OpenRead(hash))
        {
            read.Length.Should().Be(bytes.Length);
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy);
            copy.ToArray().Should().Equal(bytes);
        }

        // Ranges (resumed downloads): forward, then back.
        await using (var read = temp.Store.OpenRead(hash))
        {
            var buffer = new byte[100];
            read.Seek(123_456, SeekOrigin.Begin);
            await read.ReadExactlyAsync(buffer);
            buffer.Should().Equal(bytes.AsSpan(123_456, 100).ToArray());
            read.Seek(10, SeekOrigin.Begin);
            await read.ReadExactlyAsync(buffer);
            buffer.Should().Equal(bytes.AsSpan(10, 100).ToArray());
        }
    }

    [Fact]
    public async Task Already_compressed_and_recent_files_stay_as_they_are()
    {
        using var temp = new TempBlobStore();
        var png = new byte[64 * 1024];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        var noise = new byte[64 * 1024];
        new Random(7).NextBytes(noise);
        var fresh = Text(5_000);
        var (pngHash, noiseHash, freshHash) = (await temp.PutAsync(png), await temp.PutAsync(noise), await temp.PutAsync(fresh));
        Age(temp, pngHash);
        Age(temp, noiseHash);

        (await temp.Store.CompactAsync(TimeSpan.FromMinutes(1), default)).Files.Should().Be(0);

        foreach (var h in new[] { pngHash, noiseHash, freshHash })
            File.Exists(temp.Store.PathOf(h)).Should().BeTrue();
        File.ReadAllText(Path.Combine(temp.Root, "incompressible.txt")).Should().Contain(pngHash).And.Contain(noiseHash);
    }

    [Fact]
    public async Task Clean_ups_see_compressed_files_by_their_hash()
    {
        using var temp = new TempBlobStore();
        var (keep, drop) = (Text(3_000), Text(3_001));
        var (keepHash, dropHash) = (await temp.PutAsync(keep), await temp.PutAsync(drop));
        Age(temp, keepHash);
        Age(temp, dropHash);
        await temp.Store.CompactAsync(TimeSpan.FromMinutes(1), default);
        foreach (var h in new[] { keepHash, dropHash })
            File.SetLastWriteTimeUtc(temp.Store.PathOf(h) + ".br", DateTime.UtcNow.AddDays(-2));

        temp.Store.RemoveUnreferenced(new HashSet<string> { keepHash }).Files.Should().Be(1);

        temp.Store.Exists(keepHash).Should().BeTrue();
        temp.Store.Exists(dropHash).Should().BeFalse();
        temp.Store.RemoveIfStale(keepHash).Should().BeTrue();
        temp.Store.Exists(keepHash).Should().BeFalse();
    }

    [Fact]
    public async Task Uploading_a_file_the_store_holds_compressed_is_deduplicated()
    {
        using var temp = new TempBlobStore();
        var bytes = Text(4_000);
        var hash = await temp.PutAsync(bytes);
        Age(temp, hash);
        await temp.Store.CompactAsync(TimeSpan.FromMinutes(1), default);

        var again = await temp.Store.AppendAsync(hash, 0, bytes.Length, new MemoryStream(bytes), default);
        again.Status.Should().Be(YHDE.Server.Assets.BlobStore.AppendStatus.AlreadyStored);
        var proven = await temp.Store.AppendAsync(hash, 0, bytes.Length, new MemoryStream(bytes), default, uploader: "u1", proveBytes: true);
        proven.Status.Should().Be(YHDE.Server.Assets.BlobStore.AppendStatus.Completed);
        File.Exists(temp.Store.PathOf(hash)).Should().BeFalse("one copy is kept, the compressed one");
    }
}
