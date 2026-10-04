using MessagePack;

namespace YHDE.Server.Framing;

// Wire frame as defined in network_protocol.md.
//
// On the wire:
//   [frame_len: uint32 BE] [MessagePack-encoded Frame]
//
// The Frame is a fixed-position array so indexing is O(1) without a key scan.
// Protocol version 1. New fields are appended; existing positions are immutable.
[MessagePackObject]
public sealed class Frame
{
    public const byte ProtocolVersion = 1;

    [Key(0)] public byte Version { get; set; } = ProtocolVersion;
    [Key(1)] public MessageType MsgType { get; init; }
    [Key(2)] public Channel Channel { get; init; }
    [Key(3)] public byte Flags { get; init; }           // 0 = none; reserved for compression/fragmentation
    [Key(4)] public byte[] ClientOpRef { get; set; } = [];  // 16-byte UUID or empty
    [Key(5)] public long SeqOrAck { get; init; }        // seq (S->C) or ack watermark (C->S); 0 if unused
    [Key(6)] public byte[] Payload { get; set; } = []; // MessagePack-encoded message-specific body
}
