using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using YHDE.Server.Domain;
using YHDE.Server.Gateway;
using YHDE.Server.Operations;
using YHDE.Server.Persistence.Repositories;
using YHDE.Server.Tests.Assets;

namespace YHDE.Server.Tests.Operations;

// Server-authoritative undo (operation_system.md).
public sealed class UndoServiceTests
{
    private static readonly TempBlobStore Blobs = new();

    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid Session = Guid.NewGuid();

    private static OperationSubmission Op(string type = "ChangeProperty", string payload = """{"s":"res://a.tscn","k":"position","v":1,"o":0}""") =>
        new(Guid.NewGuid(), type, Guid.NewGuid(), payload, Guid.NewGuid(), 0);

    private static Operation Committed(Guid opId, Guid actor, long seq = 1) =>
        new(opId, seq, Branch, "ChangeProperty", Guid.NewGuid(), "{}", actor, Session, Guid.NewGuid(), 0, DateTime.UtcNow, []);

    private static (UndoService, IOperationRepository, ISessionManager) Build()
    {
        var repo = Substitute.For<IOperationRepository>();
        var sessions = Substitute.For<ISessionManager>();
        repo.CommitBatchAsync(Arg.Any<IReadOnlyList<OperationSubmission>>(), Branch, Actor, Session, Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyList<OperationSubmission>>()
                .Select((s, i) => new Operation(s.OpId, 10 + i, Branch, s.Type, s.TargetId, s.Payload, Actor, Session, s.ClientOpRef, 0, DateTime.UtcNow, []))
                .ToList());
        return (new UndoService(repo, sessions, Blobs.Store, NullLogger<UndoService>.Instance), repo, sessions);
    }

    [Fact]
    public async Task Commits_the_inverse_atomically_and_links_it_to_what_it_undoes()
    {
        var (undo, repo, sessions) = Build();
        var original = Guid.NewGuid();
        repo.GetByIdsAsync(Branch, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([Committed(original, Actor)]);
        var request = new UndoService.Request(Guid.NewGuid(), "undo", [original], [Op(), Op("RenameNode", """{"s":"res://a.tscn","n":"A","on":"B"}""")]);

        var result = await undo.ProcessAsync(request, Branch, Actor, Session, CancellationToken.None);

        result.Status.Should().Be("committed");
        result.Committed.Should().Be(2);
        await repo.Received(1).CommitBatchAsync(
            Arg.Is<IReadOnlyList<OperationSubmission>>(ops => ops.Count == 2), Branch, Actor, Session, Arg.Any<CancellationToken>());
        await sessions.Received(2).BroadcastOpCommittedAsync(Arg.Any<Operation>(), Arg.Any<CancellationToken>());

        var batch = (IReadOnlyList<OperationSubmission>)repo.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IOperationRepository.CommitBatchAsync)).GetArguments()[0]!;
        using var first = JsonDocument.Parse(batch[0].Payload);
        var u = first.RootElement.GetProperty("u");
        u.GetProperty("r").GetString().Should().Be(request.RequestId.ToString());
        u.GetProperty("k").GetString().Should().Be("undo");
        u.GetProperty("of")[0].GetString().Should().Be(original.ToString());
        first.RootElement.GetProperty("k").GetString().Should().Be("position"); // original payload kept
        using var second = JsonDocument.Parse(batch[1].Payload);
        second.RootElement.GetProperty("u").TryGetProperty("of", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Only_the_author_can_undo()
    {
        var (undo, repo, _) = Build();
        var original = Guid.NewGuid();
        repo.GetByIdsAsync(Branch, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([Committed(original, Guid.NewGuid())]);

        var result = await undo.ProcessAsync(new UndoService.Request(Guid.NewGuid(), "undo", [original], [Op()]),
            Branch, Actor, Session, CancellationToken.None);

        result.Status.Should().Be("refused");
        result.Code.Should().Be("NotAuthorized");
        await repo.DidNotReceiveWithAnyArgs().CommitBatchAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task A_resent_request_is_not_committed_twice()
    {
        var (undo, repo, _) = Build();
        var op = Op();
        repo.ExistsAsync(op.OpId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await undo.ProcessAsync(new UndoService.Request(Guid.NewGuid(), "redo", [], [op]),
            Branch, Actor, Session, CancellationToken.None);

        result.Status.Should().Be("committed");
        result.Code.Should().Be("Duplicate");
        await repo.DidNotReceiveWithAnyArgs().CommitBatchAsync(default!, default, default, default, default);
    }

    [Theory]
    [InlineData("rewind")]
    [InlineData("")]
    public async Task Rejects_unknown_kinds(string kind)
    {
        var (undo, _, _) = Build();
        var result = await undo.ProcessAsync(new UndoService.Request(Guid.NewGuid(), kind, [], [Op()]),
            Branch, Actor, Session, CancellationToken.None);
        result.Status.Should().Be("refused");
    }

    [Fact]
    public async Task Rejects_empty_and_malformed_requests()
    {
        var (undo, _, _) = Build();
        (await undo.ProcessAsync(new UndoService.Request(Guid.NewGuid(), "undo", [], []), Branch, Actor, Session, CancellationToken.None))
            .Status.Should().Be("refused");
        (await undo.ProcessAsync(new UndoService.Request(Guid.NewGuid(), "undo", [], [Op("NoSuchType")]), Branch, Actor, Session, CancellationToken.None))
            .Code.Should().Be(nameof(RejectionCode.InvalidType));
        (await undo.ProcessAsync(new UndoService.Request(Guid.NewGuid(), "undo", [], [Op(payload: "[1]")]), Branch, Actor, Session, CancellationToken.None))
            .Code.Should().Be(nameof(RejectionCode.InvalidPayload));
        var dup = Op();
        (await undo.ProcessAsync(new UndoService.Request(Guid.NewGuid(), "undo", [], [dup, dup]), Branch, Actor, Session, CancellationToken.None))
            .Status.Should().Be("refused");
    }
}
