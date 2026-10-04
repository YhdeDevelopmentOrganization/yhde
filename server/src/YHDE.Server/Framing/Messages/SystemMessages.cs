using MessagePack;

namespace YHDE.Server.Framing.Messages;

// System channel messages (network_protocol.md, Channel: system).

[MessagePackObject]
public sealed class HelloPayload
{
    [Key(0)] public byte ProtocolVersion { get; init; }
    [Key(1)] public string[] Capabilities { get; set; } = [];
    // Unused: the secret comes in the handshake's Authorization header.
    [Key(2)] public string? AuthToken { get; init; }
    // For connections without an account (invite code, server key): a random
    // id per project folder, so chat and comments know who wrote them. It is
    // self-declared; with a sign-in the account's id is used instead.
    [Key(3)] public byte[] MemberId { get; set; } = [];
    [Key(4)] public string DisplayName { get; set; } = "";
    // The add-on's version, e.g. "0.4.3", for readable mismatch messages.
    [Key(5)] public string ClientVersion { get; set; } = "";
}

[MessagePackObject]
public sealed class WelcomePayload
{
    [Key(0)] public byte NegotiatedVersion { get; init; }
    [Key(1)] public byte[] SessionId { get; set; } = []; // 16-byte UUID
    [Key(2)] public string[] ServerCapabilities { get; set; } = [];
    [Key(3)] public string ServerVersion { get; set; } = "";
    // Set when the connection came with an invite code: the one project it
    // opens, so the editor needs nothing else (projects.md).
    [Key(4)] public byte[] ProjectId { get; set; } = [];
    [Key(5)] public string ProjectName { get; set; } = "";
    [Key(6)] public byte[] BranchId { get; set; } = [];
}

[MessagePackObject]
public sealed class ErrorPayload
{
    [Key(0)] public int Code { get; init; }
    [Key(1)] public string Message { get; set; } = "";
    [Key(2)] public bool Retryable { get; init; }
}

[MessagePackObject]
public sealed class ClosePayload
{
    [Key(0)] public string Reason { get; set; } = "";
}

// Ping and Pong carry an opaque echo token for round-trip validation.
[MessagePackObject]
public sealed class PingPongPayload
{
    [Key(0)] public byte[] Token { get; set; } = [];
}
