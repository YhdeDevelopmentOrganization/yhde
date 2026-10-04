using MessagePack;

namespace YHDE.Server.Framing.Messages;

// Social channel messages (network_protocol.md, Channel: social; social.md).
// Chat and comments are people talking about the project, not project state:
// they are stored and audited, but never enter the operation log.
//
// Bodies are JSON so a request kind can gain fields without a new message
// type; every kind and field is validated by SocialService.

// SocialRequest (C->S): one action, e.g. "chat.send" or "comment.reply".
[MessagePackObject]
public sealed class SocialRequestPayload
{
    [Key(0)] public byte[] RequestId { get; set; } = []; // 16-byte UUID, echoed back
    [Key(1)] public string Kind { get; set; } = "";
    [Key(2)] public byte[] BodyJson { get; set; } = [];
}

// SocialEvent (S->C): something happened ("chat.message", "comment.thread"),
// or the answer to a request ("chat.history", "error").
[MessagePackObject]
public sealed class SocialEventPayload
{
    [Key(0)] public string Kind { get; set; } = "";
    [Key(1)] public byte[] BodyJson { get; set; } = [];
    [Key(2)] public byte[] RequestId { get; set; } = []; // set on the requester's copy
}
