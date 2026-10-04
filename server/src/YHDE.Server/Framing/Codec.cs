using System.Buffers;
using System.Buffers.Binary;
using MessagePack;

namespace YHDE.Server.Framing;

// Binary codec for YHDE frames (network_protocol.md).
//
// Wire format:
//   [frame_len: 4 bytes, big-endian uint32] [MessagePack bytes]
//
// The protocol version is agreed in Hello and Welcome.
public static class Codec
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard;

    // Encode a Frame into the length-prefixed wire format.
    public static byte[] Encode(Frame frame)
    {
        var payload = MessagePackSerializer.Serialize(frame, Options);
        var buf = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)payload.Length);
        payload.CopyTo(buf.AsSpan(4));
        return buf;
    }

    // Decode one Frame from a length-prefixed buffer (the 4-byte length header is already stripped).
    public static Frame Decode(ReadOnlyMemory<byte> msgPackBytes)
        => MessagePackSerializer.Deserialize<Frame>(msgPackBytes, Options);

    // Encode a message-specific body as a MessagePack byte array (for Frame.Payload).
    public static byte[] EncodePayload<T>(T value)
        => MessagePackSerializer.Serialize(value, Options);

    // Decode a message-specific body from Frame.Payload bytes.
    public static T DecodePayload<T>(byte[] bytes)
        => MessagePackSerializer.Deserialize<T>(bytes, Options);

    // Read exactly 4 bytes from the stream for the length header.
    public static async Task<uint?> ReadFrameLengthAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = await stream.ReadAsync(buf.AsMemory(read, 4 - read), ct);
            if (n == 0) return null; // stream closed
            read += n;
        }
        return BinaryPrimitives.ReadUInt32BigEndian(buf);
    }

    // Read exactly `length` bytes from the stream for the frame body.
    public static async Task<byte[]?> ReadFrameBodyAsync(Stream stream, uint length, CancellationToken ct, uint maxLength = 16 * 1024 * 1024)
    {
        if (length > maxLength) return null; // reject oversized frames
        var buf = new byte[length];
        var read = 0;
        while (read < (int)length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(read, (int)length - read), ct);
            if (n == 0) return null;
            read += n;
        }
        return buf;
    }
}
