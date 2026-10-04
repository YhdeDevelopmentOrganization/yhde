using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using YHDE.Server.Domain;
using YHDE.Server.Gateway;
using YHDE.Server.Operations;
using YHDE.Server.Persistence.Repositories;

namespace YHDE.Server.Tests.Operations;

// Group commit (operation_system.md): one writer per branch, many ops per
// transaction under load, broadcast in log order after the commit.
public sealed class BranchCommitterTests
{
    private static readonly Guid Branch = Guid.NewGuid();

    private static OperationSubmission Op() =>
        new(Guid.NewGuid(), OperationType.ChangeProperty, Guid.NewGuid(), """{"s":"res://a.tscn","k":"x"}""", Guid.NewGuid(), 0);

    [Fact]
    public async Task Concurrent_submissions_share_transactions_and_broadcast_in_seq_order()
    {
        var repo = Substitute.For<IOperationRepository>();
        var sessions = Substitute.For<ISessionManager>();
        var groups = new List<int>();
        var gate = new TaskCompletionSource();
        long seq = 0;
        repo.CommitGroupAsync(Arg.Any<IReadOnlyList<CommitItem>>(), Branch, Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var items = ci.Arg<IReadOnlyList<CommitItem>>();
                lock (groups) groups.Add(items.Count);
                await gate.Task; // the first group is slow; the rest pile up behind it
                return (IReadOnlyList<Operation?>)items.Select(i => (Operation?)new Operation(
                    i.Submission.OpId, Interlocked.Increment(ref seq), Branch, i.Submission.Type, i.Submission.TargetId,
                    i.Submission.Payload, i.ActorId, i.SessionId, i.Submission.ClientOpRef, 0, DateTime.UtcNow, [])).ToList();
            });
        var broadcast = new List<long>();
        sessions.BroadcastOpCommittedAsync(Arg.Any<Operation>(), Arg.Any<CancellationToken>())
            .Returns(ci => { lock (broadcast) broadcast.Add(ci.Arg<Operation>().Seq); return Task.CompletedTask; });
        var committer = new BranchCommitter(repo, sessions, NullLogger<BranchCommitter>.Instance);

        var first = committer.CommitAsync(Op(), Branch, Guid.NewGuid(), Guid.NewGuid(), default);
        await Task.Delay(50);
        var rest = Enumerable.Range(0, 20).Select(_ => committer.CommitAsync(Op(), Branch, Guid.NewGuid(), Guid.NewGuid(), default)).ToList();
        await Task.Delay(50);
        gate.SetResult();
        var results = await Task.WhenAll(rest.Prepend(first));

        results.Select(r => r!.Seq).Should().BeEquivalentTo(Enumerable.Range(1, 21).Select(i => (long)i));
        groups.Should().Equal(1, 20);
        broadcast.Should().BeInAscendingOrder().And.HaveCount(21);
    }

    [Fact]
    public async Task A_resend_is_reported_as_already_committed()
    {
        var repo = Substitute.For<IOperationRepository>();
        repo.CommitGroupAsync(Arg.Any<IReadOnlyList<CommitItem>>(), Branch, Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<Operation?>)ci.Arg<IReadOnlyList<CommitItem>>().Select(_ => (Operation?)null).ToList());
        var committer = new BranchCommitter(repo, Substitute.For<ISessionManager>(), NullLogger<BranchCommitter>.Instance);

        (await committer.CommitAsync(Op(), Branch, Guid.NewGuid(), Guid.NewGuid(), default)).Should().BeNull();
    }

    [Fact]
    public async Task A_failed_transaction_fails_its_submitters_and_the_branch_keeps_working()
    {
        var repo = Substitute.For<IOperationRepository>();
        var calls = 0;
        repo.CommitGroupAsync(Arg.Any<IReadOnlyList<CommitItem>>(), Branch, Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("db down");
                return (IReadOnlyList<Operation?>)ci.Arg<IReadOnlyList<CommitItem>>().Select(i => (Operation?)new Operation(
                    i.Submission.OpId, 1, Branch, i.Submission.Type, i.Submission.TargetId, i.Submission.Payload,
                    i.ActorId, i.SessionId, i.Submission.ClientOpRef, 0, DateTime.UtcNow, [])).ToList();
            });
        var committer = new BranchCommitter(repo, Substitute.For<ISessionManager>(), NullLogger<BranchCommitter>.Instance);

        var failing = () => committer.CommitAsync(Op(), Branch, Guid.NewGuid(), Guid.NewGuid(), default);
        await failing.Should().ThrowAsync<InvalidOperationException>();
        (await committer.CommitAsync(Op(), Branch, Guid.NewGuid(), Guid.NewGuid(), default)).Should().NotBeNull();
    }
}
