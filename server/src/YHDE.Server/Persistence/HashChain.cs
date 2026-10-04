using System.Security.Cryptography;
using System.Text;

namespace YHDE.Server.Persistence;

// Computes the tamper-evident hash-chain signature for committed operations.
// Each operation's signature incorporates the previous operation's signature,
// making any retroactive alteration detectable (database.md, security.md).
//
// signature = SHA-256( prev_signature || op_id || actor_id || branch_id
//                       || seq (8 bytes BE) || type || payload )
public static class HashChain
{
    public static byte[] ComputeSignature(
        byte[] prevSignature,
        Guid opId,
        Guid actorId,
        Guid branchId,
        long seq,
        string type,
        string payloadJson)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        sha.AppendData(prevSignature);
        sha.AppendData(opId.ToByteArray());
        sha.AppendData(actorId.ToByteArray());
        sha.AppendData(branchId.ToByteArray());

        Span<byte> seqBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(seqBytes, seq);
        sha.AppendData(seqBytes);

        sha.AppendData(Encoding.UTF8.GetBytes(type));
        sha.AppendData(Encoding.UTF8.GetBytes(payloadJson));

        return sha.GetHashAndReset();
    }

    // Verifies that a stored signature matches the inputs: used in audit/integrity checks.
    public static bool Verify(
        byte[] prevSignature,
        Guid opId, Guid actorId, Guid branchId, long seq,
        string type, string payloadJson,
        byte[] expectedSignature)
    {
        var computed = ComputeSignature(prevSignature, opId, actorId, branchId, seq, type, payloadJson);
        return CryptographicOperations.FixedTimeEquals(computed, expectedSignature);
    }
}
