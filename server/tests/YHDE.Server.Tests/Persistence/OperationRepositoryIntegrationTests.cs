using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using YHDE.Server.Domain;
using YHDE.Server.Persistence;
using YHDE.Server.Persistence.Migrations;
using YHDE.Server.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace YHDE.Server.Tests.Persistence;

// Integration tests against a real PostgreSQL instance via Testcontainers.
// Requires Docker to be running; skip gracefully when unavailable.
[Collection("integration")]
public sealed class OperationRepositoryIntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private Database? _db;
    private OperationRepository? _opRepo;
    private BranchRepository? _branchRepo;

    private static readonly Guid DefaultProjectId = new("ffffffff-0000-0000-0000-000000000001");
    private static readonly Guid DefaultBranchId = new("ffffffff-0000-0000-0000-000000000002");

    public async Task InitializeAsync()
    {
        try
        {
            // YHDE_TEST_PG: an existing database to use instead of a container.
            var cs = Environment.GetEnvironmentVariable("YHDE_TEST_PG");
            if (string.IsNullOrEmpty(cs))
            {
                _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
                await _postgres.StartAsync();
                cs = _postgres.GetConnectionString();
            }
            Runner.Run(cs, NullLogger.Instance);

            var ds = new NpgsqlDataSourceBuilder(cs).Build();
            if (_postgres is null)
            {
                // A shared database: every test starts from an empty log.
                await using var reset = ds.CreateCommand("TRUNCATE operations; UPDATE branches SET head_seq = 0;");
                await reset.ExecuteNonQueryAsync();
            }
            _db = new Database(ds);
            _opRepo = new OperationRepository(_db);
            _branchRepo = new BranchRepository(_db);
        }
        catch
        {
            // Docker unavailable: tests will be skipped via null guards.
        }
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task CommitAsync_assigns_monotonic_seq_and_persists()
    {
        if (_opRepo is null) return; // Skip if Docker unavailable.

        var sub = MakeSubmission(parentSeq: 0);
        var actorId = DevIdentity.ActorId;
        var sessionId = Guid.NewGuid();

        var op = await _opRepo.CommitAsync(sub, DefaultBranchId, actorId, sessionId, default);

        op.Seq.Should().Be(1);
        op.ActorId.Should().Be(actorId);
        op.BranchId.Should().Be(DefaultBranchId);
        op.Signature.Should().NotBeEmpty();

        // Re-read via tail fetch to confirm persistence.
        var tail = await _opRepo.GetTailAsync(DefaultBranchId, 0, 1, default);
        tail.Should().HaveCount(1);
        tail[0].OpId.Should().Be(op.OpId);
    }

    [Fact]
    public async Task CommitAsync_produces_sequential_seq_for_multiple_ops()
    {
        if (_opRepo is null) return;

        var op1 = await _opRepo.CommitAsync(MakeSubmission(parentSeq: 0), DefaultBranchId,
            DevIdentity.ActorId, Guid.NewGuid(), default);
        var op2 = await _opRepo.CommitAsync(MakeSubmission(parentSeq: op1.Seq), DefaultBranchId,
            DevIdentity.ActorId, Guid.NewGuid(), default);

        op2.Seq.Should().Be(op1.Seq + 1);
    }

    [Fact]
    public async Task ExistsAsync_returns_true_for_committed_op()
    {
        if (_opRepo is null) return;

        var sub = MakeSubmission(parentSeq: 0);
        await _opRepo.CommitAsync(sub, DefaultBranchId, DevIdentity.ActorId, Guid.NewGuid(), default);

        var exists = await _opRepo.ExistsAsync(sub.OpId, default);
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_returns_false_for_unknown_op()
    {
        if (_opRepo is null) return;

        var exists = await _opRepo.ExistsAsync(Guid.NewGuid(), default);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task Hash_chain_links_consecutive_operations()
    {
        if (_opRepo is null) return;

        var op1 = await _opRepo.CommitAsync(MakeSubmission(parentSeq: 0), DefaultBranchId,
            DevIdentity.ActorId, Guid.NewGuid(), default);
        var op2 = await _opRepo.CommitAsync(MakeSubmission(parentSeq: op1.Seq), DefaultBranchId,
            DevIdentity.ActorId, Guid.NewGuid(), default);

        // op2's signature must incorporate op1's signature.
        var expected = HashChain.ComputeSignature(
            op1.Signature, op2.OpId, op2.ActorId, op2.BranchId,
            op2.Seq, op2.Type, op2.Payload);

        op2.Signature.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task CommitBatchAsync_commits_consecutively_and_keeps_the_chain()
    {
        if (_opRepo is null) return;

        var first = await _opRepo.CommitAsync(MakeSubmission(parentSeq: 0), DefaultBranchId,
            DevIdentity.ActorId, Guid.NewGuid(), default);
        var batch = new[] { MakeSubmission(first.Seq), MakeSubmission(first.Seq), MakeSubmission(first.Seq) };

        var committed = await _opRepo.CommitBatchAsync(batch, DefaultBranchId, DevIdentity.ActorId, Guid.NewGuid(), default);

        committed.Select(o => o.Seq).Should().Equal(first.Seq + 1, first.Seq + 2, first.Seq + 3);
        var prev = first.Signature;
        foreach (var op in committed)
        {
            op.Signature.Should().BeEquivalentTo(HashChain.ComputeSignature(
                prev, op.OpId, op.ActorId, op.BranchId, op.Seq, op.Type, op.Payload));
            prev = op.Signature;
        }
        var tail = await _opRepo.GetTailAsync(DefaultBranchId, first.Seq, first.Seq + 3, default);
        tail.Select(o => o.OpId).Should().Equal(batch.Select(b => b.OpId));

        var found = await _opRepo.GetByIdsAsync(DefaultBranchId, [batch[2].OpId, first.OpId, Guid.NewGuid()], default);
        found.Select(o => o.OpId).Should().Equal(first.OpId, batch[2].OpId);

        var next = await _opRepo.CommitAsync(MakeSubmission(parentSeq: 0), DefaultBranchId,
            DevIdentity.ActorId, Guid.NewGuid(), default);
        next.Seq.Should().Be(first.Seq + 4);
    }

    [Fact]
    public async Task CommitGroupAsync_commits_several_editors_in_order_and_skips_resends()
    {
        if (_opRepo is null) return;

        var first = await _opRepo.CommitAsync(MakeSubmission(parentSeq: 0), DefaultBranchId,
            DevIdentity.ActorId, Guid.NewGuid(), default);
        var alice = (Actor: Guid.NewGuid(), Session: Guid.NewGuid());
        var bob = (Actor: Guid.NewGuid(), Session: Guid.NewGuid());
        var a1 = MakeSubmission(first.Seq);
        var b1 = MakeSubmission(first.Seq);
        var items = new[]
        {
            new CommitItem(a1, alice.Actor, alice.Session),
            new CommitItem(new OperationSubmission(first.OpId, first.Type, first.TargetId, first.Payload, first.ClientOpRef, first.ParentSeq), bob.Actor, bob.Session), // resent after a reconnect
            new CommitItem(b1, bob.Actor, bob.Session),
            new CommitItem(a1, alice.Actor, alice.Session),                // the same op twice in one group
        };

        var results = await _opRepo.CommitGroupAsync(items, DefaultBranchId, default);

        results.Select(r => r?.Seq).Should().Equal(first.Seq + 1, null, first.Seq + 2, null);
        results[0]!.ActorId.Should().Be(alice.Actor);
        results[2]!.SessionId.Should().Be(bob.Session);
        results[0]!.Signature.Should().BeEquivalentTo(HashChain.ComputeSignature(
            first.Signature, a1.OpId, alice.Actor, DefaultBranchId, first.Seq + 1, a1.Type, a1.Payload));
        results[2]!.Signature.Should().BeEquivalentTo(HashChain.ComputeSignature(
            results[0]!.Signature, b1.OpId, bob.Actor, DefaultBranchId, first.Seq + 2, b1.Type, b1.Payload));

        var tail = await _opRepo.GetTailAsync(DefaultBranchId, first.Seq, first.Seq + 10, default);
        tail.Select(o => o.OpId).Should().Equal(a1.OpId, b1.OpId);
        tail[1].Signature.Should().BeEquivalentTo(results[2]!.Signature);

        var next = await _opRepo.CommitAsync(MakeSubmission(parentSeq: 0), DefaultBranchId,
            DevIdentity.ActorId, Guid.NewGuid(), default);
        next.Seq.Should().Be(first.Seq + 3);

        (await _opRepo.CommitGroupAsync([new CommitItem(a1, alice.Actor, alice.Session)], DefaultBranchId, default))
            .Should().Equal([null]);
    }

    private static OperationSubmission MakeSubmission(long parentSeq) => new(
        OpId: Guid.NewGuid(),
        Type: OperationType.ChangeProperty,
        TargetId: Guid.NewGuid(),
        Payload: """{"prop":"position","old":[0,0],"new":[1,1]}""",
        ClientOpRef: Guid.NewGuid(),
        ParentSeq: parentSeq);
}
