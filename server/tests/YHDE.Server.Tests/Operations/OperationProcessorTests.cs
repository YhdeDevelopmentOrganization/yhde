using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using YHDE.Server.Domain;
using YHDE.Server.Gateway;
using YHDE.Server.Operations;
using YHDE.Server.Persistence.Repositories;
using YHDE.Server.Tests.Assets;

namespace YHDE.Server.Tests.Operations;

// Unit tests for OperationProcessor.
// Uses mocked repository interfaces so no database is required.
// Integration tests (Persistence/OperationRepositoryIntegrationTests.cs)
// cover the full durable-commit path against a real PostgreSQL instance.
public sealed class OperationProcessorTests
{
    private static readonly TempBlobStore Blobs = new();

    private static readonly Guid BranchId = Guid.NewGuid();
    private static readonly Guid ActorId = DevIdentity.ActorId;
    private static readonly Guid SessionId = Guid.NewGuid();

    private static OperationSubmission ValidSubmission() => new(
        OpId: Guid.NewGuid(),
        Type: OperationType.ChangeProperty,
        TargetId: Guid.NewGuid(),
        Payload: """{"prop":"position","old":[0,0],"new":[10,0]}""",
        ClientOpRef: Guid.NewGuid(),
        ParentSeq: 0);

    [Fact]
    public async Task Valid_operation_is_committed_and_returns_Committed()
    {
        var (processor, _, sessions) = BuildProcessor(existsReturn: false);
        var sub = ValidSubmission();

        var result = await processor.ProcessAsync(sub, BranchId, ActorId, SessionId, default);

        result.Should().BeOfType<SubmitResult.Committed>()
            .Which.Operation.Seq.Should().Be(1);
        await sessions.Received(1).BroadcastOpCommittedAsync(
            Arg.Is<Operation>(o => o.OpId == sub.OpId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_operation_type_is_rejected_with_InvalidType()
    {
        var (processor, _, _) = BuildProcessor(existsReturn: false);
        var sub = ValidSubmission() with { Type = "FlyToMoon" };

        var result = await processor.ProcessAsync(sub, BranchId, ActorId, SessionId, default);

        result.Should().BeOfType<SubmitResult.Rejected>()
            .Which.Code.Should().Be(RejectionCode.InvalidType);
    }

    [Fact]
    public async Task Empty_target_id_is_rejected_with_InvalidTargetId()
    {
        var (processor, _, _) = BuildProcessor(existsReturn: false);
        var sub = ValidSubmission() with { TargetId = Guid.Empty };

        var result = await processor.ProcessAsync(sub, BranchId, ActorId, SessionId, default);

        result.Should().BeOfType<SubmitResult.Rejected>()
            .Which.Code.Should().Be(RejectionCode.InvalidTargetId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public async Task Invalid_payload_is_rejected_with_InvalidPayload(string payload)
    {
        var (processor, _, _) = BuildProcessor(existsReturn: false);
        var sub = ValidSubmission() with { Payload = payload };

        var result = await processor.ProcessAsync(sub, BranchId, ActorId, SessionId, default);

        result.Should().BeOfType<SubmitResult.Rejected>()
            .Which.Code.Should().Be(RejectionCode.InvalidPayload);
    }

    [Fact]
    public async Task Duplicate_op_id_returns_DuplicateOpId_without_committing()
    {
        var (processor, _, sessions) = BuildProcessor(existsReturn: true);
        var sub = ValidSubmission();

        var result = await processor.ProcessAsync(sub, BranchId, ActorId, SessionId, default);

        result.Should().BeOfType<SubmitResult.Rejected>()
            .Which.Code.Should().Be(RejectionCode.DuplicateOpId);

        await sessions.DidNotReceive().BroadcastOpCommittedAsync(Arg.Any<Operation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateNode_is_valid_operation_type()
    {
        var (processor, _, _) = BuildProcessor(existsReturn: false);
        var sub = ValidSubmission() with
        {
            Type = OperationType.CreateNode,
            Payload = """{"nodeType":"Node3D","parentId":"00000000-0000-0000-0000-000000000001","name":"Enemy"}""",
        };

        var result = await processor.ProcessAsync(sub, BranchId, ActorId, SessionId, default);

        result.Should().BeOfType<SubmitResult.Committed>();
    }

    [Fact]
    public async Task A_file_registered_again_with_the_same_bytes_is_not_logged_twice()
    {
        var (processor, opRepo, _) = BuildProcessor(existsReturn: false);
        var hash = await Blobs.PutAsync([7, 7, 7]);
        var target = Guid.NewGuid();
        var first = new OperationSubmission(Guid.NewGuid(), OperationType.RegisterAsset, target,
            $$"""{"s":"res://same.png","h":"{{hash}}","n":3}""", Guid.NewGuid(), 0);
        opRepo.GetLatestAssetOpAsync(BranchId, target, Arg.Any<CancellationToken>()).Returns(MakeCommitted(first));
        var second = first with { OpId = Guid.NewGuid(), ClientOpRef = Guid.NewGuid() };

        var result = await processor.ProcessAsync(second, BranchId, ActorId, SessionId, default);

        result.Should().BeOfType<SubmitResult.Rejected>().Which.Code.Should().Be(RejectionCode.AlreadyCurrent);
        await opRepo.DidNotReceiveWithAnyArgs().CommitGroupAsync(default!, default, default);
    }

    [Fact]
    public async Task A_file_update_with_new_bytes_or_a_delete_of_a_live_file_is_logged()
    {
        var (processor, opRepo, _) = BuildProcessor(existsReturn: false);
        var oldHash = await Blobs.PutAsync([1]);
        var newHash = await Blobs.PutAsync([2]);
        var target = Guid.NewGuid();
        var registered = new OperationSubmission(Guid.NewGuid(), OperationType.RegisterAsset, target,
            $$"""{"s":"res://f.png","h":"{{oldHash}}","n":1}""", Guid.NewGuid(), 0);
        opRepo.GetLatestAssetOpAsync(BranchId, target, Arg.Any<CancellationToken>()).Returns(MakeCommitted(registered));
        var update = new OperationSubmission(Guid.NewGuid(), OperationType.UpdateAsset, target,
            $$"""{"s":"res://f.png","h":"{{newHash}}","n":1,"o":"{{oldHash}}"}""", Guid.NewGuid(), 0);
        var delete = new OperationSubmission(Guid.NewGuid(), OperationType.DeleteAsset, target,
            $$"""{"s":"res://f.png","o":"{{oldHash}}"}""", Guid.NewGuid(), 0);

        (await processor.ProcessAsync(update, BranchId, ActorId, SessionId, default)).Should().BeOfType<SubmitResult.Committed>();
        (await processor.ProcessAsync(delete, BranchId, ActorId, SessionId, default)).Should().BeOfType<SubmitResult.Committed>();
    }

    // Helpers

    private static Operation MakeCommitted(OperationSubmission sub, long seq = 1) => new(
        sub.OpId, seq, BranchId, sub.Type, sub.TargetId,
        sub.Payload, ActorId, SessionId, sub.ClientOpRef, sub.ParentSeq,
        DateTime.UtcNow, []);

    private static (OperationProcessor, IOperationRepository, ISessionManager)
        BuildProcessor(bool existsReturn)
    {
        var opRepo = Substitute.For<IOperationRepository>();
        var sm = Substitute.For<ISessionManager>();

        // A duplicate comes back as null; otherwise seqs follow the group.
        opRepo.CommitGroupAsync(Arg.Any<IReadOnlyList<CommitItem>>(), BranchId, Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyList<CommitItem>>()
                .Select((item, i) => existsReturn ? null : MakeCommitted(item.Submission, i + 1))
                .ToList());
        sm.BroadcastOpCommittedAsync(Arg.Any<Operation>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var committer = new BranchCommitter(opRepo, sm, NullLogger<BranchCommitter>.Instance);
        var processor = new OperationProcessor(committer, opRepo, Blobs.Store, NullLogger<OperationProcessor>.Instance);

        return (processor, opRepo, (ISessionManager)sm);
    }
}
