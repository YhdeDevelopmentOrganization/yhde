namespace YHDE.Server.Framing;

// Logical channels as defined in network_protocol.md.
public enum Channel : byte
{
    System = 0x00,
    Ops = 0x01,
    Presence = 0x02,
    AssetsCtrl = 0x03,
    Social = 0x04,
}
