using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using YHDE.Server.Domain;
using YHDE.Server.Operations;
using YHDE.Server.Persistence.Repositories;
using YHDE.Server.Tests.Assets;
using YHDE.Server.Text;

namespace YHDE.Server.Tests.Text;

// The server side of live text editing (text_editing.md): edits made on old
// versions are brought up to date; what cannot be merged is refused.
public sealed class TextDocumentsTests : IDisposable
{
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly Guid Target = Guid.NewGuid();
    private const string Path = "res://player.gd";

    private readonly TempBlobStore _blobs = new();
    private readonly IOperationRepository _repo = Substitute.For<IOperationRepository>();
    private readonly TextDocuments _docs;
    private long _seq = 10;

    public TextDocumentsTests()
    {
        _docs = new TextDocuments(_repo, _blobs.Store, NullLogger<TextDocuments>.Instance);
    }

    public void Dispose() => _blobs.Dispose();

    private async Task RegisterAsync(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = await _blobs.PutAsync(bytes);
        var op = new Operation(Guid.NewGuid(), _seq, Branch, OperationType.RegisterAsset, Target,
            $$"""{"s":"{{Path}}","h":"{{hash}}","n":{{bytes.Length}}}""", Guid.Empty, Guid.Empty, Guid.NewGuid(), 0, DateTime.UtcNow, []);
        _repo.GetLatestAssetOpAsync(Branch, Target, Arg.Any<CancellationToken>()).Returns(op);
        _repo.GetTargetOpsAsync(Branch, Target, Arg.Any<long>(), OperationType.EditText, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Operation>());
    }

    private static OperationSubmission Edit(long baseSeq, string ops, bool save = false) =>
        new(Guid.NewGuid(), OperationType.EditText, Target,
            $$"""{"s":"{{Path}}","b":{{baseSeq}},"t":{{ops}}{{(save ? ",\"save\":true" : "")}}}""", Guid.NewGuid(), 0);

    // Prepare + "commit" (next seq) + apply, like the processor does.
    private async Task<(long Seq, JsonElement Payload)?> SubmitAsync(OperationSubmission s)
    {
        var (ready, _, _) = await _docs.PrepareAsync(s, Branch, default);
        if (ready is null) return null;
        _seq++;
        _docs.Applied(Branch, ready, _seq);
        using var doc = JsonDocument.Parse(ready.Submission.Payload);
        return (_seq, doc.RootElement.Clone());
    }

    [Fact]
    public async Task Two_people_typing_on_the_same_version_both_land()
    {
        await RegisterAsync("var speed = 1\n");
        var first = await SubmitAsync(Edit(10, """[12, "0", 2]"""));     // speed = 10
        var second = await SubmitAsync(Edit(10, """["# tuned\n", 14]""")); // made without seeing the first

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        second!.Value.Payload.GetProperty("b").GetInt64().Should().Be(first!.Value.Seq);
        second.Value.Payload.GetProperty("t").GetRawText().Should().Be("""["# tuned\n",15]""");
    }

    [Fact]
    public async Task Crlf_files_are_edited_as_plain_lines()
    {
        await RegisterAsync("a\r\nb\r\n");
        (await SubmitAsync(Edit(10, """[4, "c\n"]"""))).Should().NotBeNull();
    }

    [Fact]
    public async Task An_edit_that_does_not_fit_is_refused_as_outdated()
    {
        await RegisterAsync("abc");
        var (ready, code, _) = await _docs.PrepareAsync(Edit(10, """[5, "x"]"""), Branch, default);
        ready.Should().BeNull();
        code.Should().Be(RejectionCode.TextOutdated);
    }

    [Fact]
    public async Task Edits_from_before_the_file_was_replaced_are_refused()
    {
        await RegisterAsync("abc");
        var (ready, code, _) = await _docs.PrepareAsync(Edit(9, """[3, "x"]"""), Branch, default);
        ready.Should().BeNull();
        code.Should().Be(RejectionCode.TextOutdated);
    }

    [Fact]
    public async Task A_version_from_the_future_is_invalid()
    {
        await RegisterAsync("abc");
        var (ready, code, _) = await _docs.PrepareAsync(Edit(99, """[3]"""), Branch, default);
        ready.Should().BeNull();
        code.Should().Be(RejectionCode.InvalidPayload);
    }

    [Fact]
    public async Task A_save_marker_is_kept_in_the_logged_edit()
    {
        await RegisterAsync("abc");
        var saved = await SubmitAsync(Edit(10, "[3]", save: true));
        saved!.Value.Payload.GetProperty("save").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Binary_files_and_unknown_files_are_not_text()
    {
        var bytes = new byte[] { 0xff, 0xfe, 0x00, 0x80 };
        var hash = await _blobs.PutAsync(bytes);
        var op = new Operation(Guid.NewGuid(), 10, Branch, OperationType.RegisterAsset, Target,
            $$"""{"s":"{{Path}}","h":"{{hash}}","n":4}""", Guid.Empty, Guid.Empty, Guid.NewGuid(), 0, DateTime.UtcNow, []);
        _repo.GetLatestAssetOpAsync(Branch, Target, Arg.Any<CancellationToken>()).Returns(op);
        (await _docs.PrepareAsync(Edit(10, "[4]"), Branch, default)).Ready.Should().BeNull();

        _repo.GetLatestAssetOpAsync(Branch, Target, Arg.Any<CancellationToken>()).Returns((Operation?)null);
        _docs.Invalidate(Branch, Path);
        (await _docs.PrepareAsync(Edit(10, "[4]"), Branch, default)).Ready.Should().BeNull();
    }

    [Fact]
    public async Task Rebuilds_the_text_from_the_file_and_the_logged_edits()
    {
        await RegisterAsync("hello");
        var logged = new Operation(Guid.NewGuid(), 11, Branch, OperationType.EditText, Target,
            $$"""{"s":"{{Path}}","b":10,"t":[5," world"]}""", Guid.Empty, Guid.Empty, Guid.NewGuid(), 10, DateTime.UtcNow, []);
        _repo.GetTargetOpsAsync(Branch, Target, 10, OperationType.EditText, Arg.Any<CancellationToken>()).Returns([logged]);
        _seq = 11;

        var next = await SubmitAsync(Edit(10, """["> ", 5]""")); // made on "hello" before " world" landed
        next!.Value.Payload.GetProperty("t").GetRawText().Should().Be("""["> ",11]""");
    }
}
