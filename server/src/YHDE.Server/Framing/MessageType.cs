namespace YHDE.Server.Framing;

// Wire message types as defined in network_protocol.md.
// Values are fixed; new types may only be added, never repurposed.
public enum MessageType : byte
{
    // Channel: system
    Hello = 0x01,
    Welcome = 0x02,
    Ping = 0x03,
    Pong = 0x04,
    Error = 0x05,
    Close = 0x06,

    // Channel: ops
    Subscribe = 0x10,
    SyncState = 0x11,
    SubmitOp = 0x12,
    OpCommitted = 0x13,
    OpRejected = 0x14,
    UndoRequest = 0x15,
    Ack = 0x16,
    UndoResult = 0x17,

    // Channel: presence
    PresenceUpdate = 0x20,
    PresenceState = 0x21,

    // Channel: assets-ctrl
    AssetNeed = 0x30,
    AssetHave = 0x31,
    AssetUploadInit = 0x32,

    // Channel: social (chat and comments; social.md)
    SocialRequest = 0x40,
    SocialEvent = 0x41,
}
