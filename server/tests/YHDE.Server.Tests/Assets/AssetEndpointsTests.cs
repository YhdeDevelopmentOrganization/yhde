using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using YHDE.Server.Assets;

namespace YHDE.Server.Tests.Assets;

// The HTTP side of the asset plane (assets.md).
public sealed class AssetEndpointsTests : IDisposable
{
    private readonly TempBlobStore _temp = new();
    // The operator's server key without a project: the whole store.
    private static readonly AssetScope Server = AssetScope.Server();
    private readonly InMemoryProjectBlobs _held = new();

    public void Dispose() => _temp.Dispose();

    private static DefaultHttpContext Patch(byte[] body, long offset, long total)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Patch;
        context.Request.Headers[AssetEndpoints.UploadOffsetHeader] = offset.ToString();
        context.Request.Headers[AssetEndpoints.UploadLengthHeader] = total.ToString();
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        return context;
    }

    [Fact]
    public async Task Uploads_in_chunks_then_reports_the_blob_as_present()
    {
        var bytes = Encoding.UTF8.GetBytes("0123456789abcdef");
        var hash = TempBlobStore.HashOf(bytes);

        var missing = (await AssetEndpoints.MissingAsync(new([hash]), _temp.Store, Server, _held, default));
        missing.Should().BeOfType<Ok<AssetEndpoints.MissingResponse>>()
            .Which.Value!.Missing.Should().Equal(hash);

        var first = Patch(bytes[..6], 0, bytes.Length);
        (await AssetEndpoints.AppendAsync(hash, _temp.Store, first, Server, _held)).Should().BeOfType<Ok<AssetEndpoints.ChunkResponse>>()
            .Which.Value.Should().Be(new AssetEndpoints.ChunkResponse(6, false));
        first.Response.Headers[AssetEndpoints.UploadOffsetHeader].ToString().Should().Be("6");

        var partial = (await AssetEndpoints.MissingAsync(new([hash]), _temp.Store, Server, _held, default)) as Ok<AssetEndpoints.MissingResponse>;
        partial!.Value!.Partial.Should().ContainKey(hash).WhoseValue.Should().Be(6);

        (await AssetEndpoints.AppendAsync(hash, _temp.Store, Patch(bytes[6..], 6, bytes.Length), Server, _held))
            .Should().BeOfType<Ok<AssetEndpoints.ChunkResponse>>()
            .Which.Value.Should().Be(new AssetEndpoints.ChunkResponse(bytes.Length, true));

        var head = new DefaultHttpContext();
        (await AssetEndpoints.HeadAsync(hash, _temp.Store, head, Server, _held)).Should().BeOfType<Ok>();
        head.Response.ContentLength.Should().Be(bytes.Length);
        ((await AssetEndpoints.MissingAsync(new([hash]), _temp.Store, Server, _held, default)) as Ok<AssetEndpoints.MissingResponse>)!.Value!.Missing.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wrong_offset_is_a_conflict_that_names_the_right_one()
    {
        var bytes = new byte[10];
        var hash = TempBlobStore.HashOf(bytes);

        var result = await AssetEndpoints.AppendAsync(hash, _temp.Store, Patch(bytes[..5], 3, bytes.Length), Server, _held);

        result.Should().BeOfType<Conflict<AssetEndpoints.ChunkResponse>>()
            .Which.Value!.Offset.Should().Be(0);
    }

    [Fact]
    public async Task Rejects_bad_names_missing_headers_and_tampered_bytes()
    {
        (await AssetEndpoints.AppendAsync("nothex", _temp.Store, Patch([1], 0, 1), Server, _held))
            .Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(400);

        var noHeaders = new DefaultHttpContext();
        (await AssetEndpoints.AppendAsync(TempBlobStore.HashOf([1]), _temp.Store, noHeaders, Server, _held))
            .Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(400);

        (await AssetEndpoints.AppendAsync(TempBlobStore.HashOf([1]), _temp.Store, Patch([2], 0, 1), Server, _held))
            .Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(422);

        (await AssetEndpoints.MissingAsync(new(["../x"]), _temp.Store, Server, _held, default))
            .Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Serves_stored_bytes_and_404s_unknown_ones()
    {
        var hash = await _temp.PutAsync([7, 7, 7]);

        (await AssetEndpoints.GetAsync(hash, _temp.Store, Server, _held, default)).Should().BeOfType<FileStreamHttpResult>()
            .Which.EnableRangeProcessing.Should().BeTrue();
        (await AssetEndpoints.GetAsync(TempBlobStore.HashOf([1]), _temp.Store, Server, _held, default)).Should().BeOfType<NotFound>();
    }
}
