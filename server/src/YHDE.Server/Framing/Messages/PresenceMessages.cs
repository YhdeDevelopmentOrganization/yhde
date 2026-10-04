using MessagePack;

namespace YHDE.Server.Framing.Messages;

// Presence channel messages (network_protocol.md, Channel: presence; presence.md).
// Presence is ephemeral: it is never persisted and never enters the operation log.

// PresenceUpdate (C->S): the sender's current awareness state.
// Who sent it is not in the payload: the server adds it from the session. For
// a signed-in editor the account's name replaces DisplayName.
[MessagePackObject]
public sealed class PresenceUpdatePayload
{
    [Key(0)] public string DisplayName { get; set; } = "";
    [Key(1)] public string Scene { get; set; } = "";      // res:// path of the scene being viewed
    [Key(2)] public string Tool { get; set; } = "";       // active editor screen/tool ("2D", "3D", "Script", …)
    [Key(3)] public string StateJson { get; set; } = "{}"; // selection, cursor, view, live drags (JSON object, bounded)
}

// One peer's presence as fanned out by the server.
[MessagePackObject]
public sealed class PresenceEntryPayload
{
    [Key(0)] public byte[] SessionId { get; set; } = [];
    [Key(1)] public byte[] ActorId { get; set; } = [];
    [Key(2)] public string DisplayName { get; set; } = "";
    [Key(3)] public string Scene { get; set; } = "";
    [Key(4)] public string Tool { get; set; } = "";
    [Key(5)] public string StateJson { get; set; } = "{}";
    [Key(6)] public long UpdatedAtUnixMs { get; set; }
    // Who this is across sessions (Hello.MemberId): the address for direct messages.
    [Key(7)] public byte[] MemberId { get; set; } = [];
}

// PresenceState (S->C): either a full snapshot of the branch's peers (sent on
// Subscribe) or a delta (entries that changed and sessions that left).
[MessagePackObject]
public sealed class PresenceStatePayload
{
    [Key(0)] public PresenceEntryPayload[] Entries { get; set; } = [];
    [Key(1)] public byte[][] Left { get; set; } = [];
    [Key(2)] public bool Full { get; set; }
}
