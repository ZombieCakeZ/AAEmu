using System.Security.Cryptography;

namespace AAEmu.Commons.Cryptography;

/// <summary>
/// Per-connection encryption state for the 2.0.1.7 Trion AA wire protocol.
/// Ported from the 2.0.1.7 alpha emulator (uranusq / Nikes / NL0bP).
///
/// Lifecycle:
///   1. Server generates a fresh RSA key pair on first connection and sends the
///      public modulus + exponent in the X2EnterWorldResponsePacket pubKey field.
///   2. Client encrypts a 16-byte AES key + a 4-byte XOR seed with that public
///      key and sends them back via CSAesXorKeyPacket.
///   3. From that point on, Level-5 packets are encrypted with the AES key
///      (CBC, no padding, 128-bit) plus a custom XOR layer that mutates the
///      Offset / SecondaryOffset sequences per packet.
/// </summary>
public class ConnectionKeychain
{
    public uint ConnectionId { get; set; }
    public RSACryptoServiceProvider RsaKeyPair { get; set; }
    public bool ReceivedKeys { get; set; }
    public byte[] AesKey { get; set; }
    public byte[] IV { get; set; }
    public uint XorKey { get; set; }
    public byte SCMessageCount { get; set; }
    public byte CSMessageCount { get; set; }
    public byte CSOffsetSequence { get; set; }
    public uint CSSecondaryOffsetSequence { get; set; }

    public ConnectionKeychain(uint connectionId, RSACryptoServiceProvider rsaKeyPair)
    {
        ConnectionId = connectionId;
        RsaKeyPair = rsaKeyPair;
        AesKey = new byte[16];
        IV = new byte[16];
        XorKey = 0;
    }
}
