using YHDE.Server.Domain;
using YHDE.Server.Framing;
using YHDE.Server.Framing.Messages;

namespace YHDE.Server.Gateway;

public interface ISessionManager
{
    void Register(SessionState session);
    void Remove(Guid sessionId);
    IEnumerable<SessionState> GetBranchSubscribers(Guid branchId);
    IEnumerable<SessionState> GetProjectSessions(Guid projectId);
    Task BroadcastOpCommittedAsync(Operation op, CancellationToken ct);
    Task SendRejectedAsync(SessionState session, Guid clientOpRef, Guid opId, string code, string reason, CancellationToken ct);
    Task SendWelcomeAsync(SessionState session, CancellationToken ct, Projects.ProjectInfo? project = null);
    IReadOnlyCollection<SessionState> All { get; }
    Task SendSyncStateAsync(SessionState session, Guid branchId, long headSeq, IReadOnlyList<CommittedOpPayload> tailOps, bool hasMore, CancellationToken ct);
    Task SendFrameAsync(SessionState session, Frame frame, CancellationToken ct);
    // Already encoded (Codec.Encode): one encoding for many recipients.
    // `droppable`: a cursor update that may be skipped for a lagging client.
    Task SendEncodedAsync(SessionState session, byte[] wire, CancellationToken ct, bool droppable = false);
    // Cuts a connection off (too slow, idle, or rights withdrawn); the
    // editor reconnects and catches up from the log.
    // `slow`: counted as a slow client on the admin page.
    void Disconnect(SessionState session, string reason, bool slow = false);
    Task SendPongAsync(SessionState session, byte[] token, CancellationToken ct);
    Task SendErrorAsync(SessionState session, int code, string message, bool retryable, CancellationToken ct);
}
