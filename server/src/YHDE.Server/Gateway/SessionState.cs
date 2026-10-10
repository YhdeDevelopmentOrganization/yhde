using System.Net.WebSockets;

namespace YHDE.Server.Gateway;

// Ephemeral per-connection state (network_protocol.md).
// Session state lives only in memory; losing it (crash/disconnect) loses no committed data.
// All authoritative data lives in the operation log (database.md).
public sealed class SessionState
{
    public Guid SessionId { get; } = Guid.NewGuid();
    public Guid ActorId { get; init; }

    // Which projects this connection may open, and for a sign-in who it is.
    public AccessGrant Grant { get; init; } = AccessGrant.AllProjects;

    // The person behind the session for chat and comments: the account when
    // signed in, otherwise from Hello. Until then it is the session itself.
    public Guid MemberId { get; set; }
    public string MemberName { get; set; } = "Anonymous";
    public string ClientVersion { get; set; } = "";
    public WebSocket Socket { get; init; } = null!;

    // Set after Subscribe: until then the session has no branch subscription.
    public Guid? SubscribedProjectId { get; set; }
    public Guid? SubscribedBranchId { get; set; }

    // Last seq the client has acknowledged (Ack watermark, network_protocol.md).
    public long LastAckedSeq { get; set; }

    // Prevents concurrent writes to the same WebSocket.
    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    // Everything sent to this connection goes through here (Outbox.cs).
    public Outbox Outbox { get; init; } = new();

    // When the last message arrived (Environment.TickCount64): a connection
    // silent for longer than the idle timeout is closed (SessionSweeper).
    public long LastReceivedTicks { get; set; } = Environment.TickCount64;

    public bool IsSubscribed => SubscribedBranchId.HasValue;
}
