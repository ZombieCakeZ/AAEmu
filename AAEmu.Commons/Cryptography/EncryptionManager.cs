using System.Collections.Concurrent;
using System.Security.Cryptography;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;

using NLog;

namespace AAEmu.Commons.Cryptography;

/// <summary>
/// 2.0.1.7 Trion-client encryption layer. Ported from the 2.0.1.7 alpha
/// emulator (uranusq / Nikes / NL0bP) with the version-specific magic
/// constants for build r249376 (2015-09-18):
///
///   XOR mix (CSAesXorKey storeClientKeys): head = (head ^ 0x15A02442) * head ^ 0x70F1F23
///   Server XOR seed:                       cry  = length ^ 0x1F2175A0
///   Client XOR seed:                       cry  = mul ^ ((MakeSeq + 0x75A02458) ^ 0x3458B610)
///
/// Other AA versions use different mix constants — do NOT copy this manager
/// verbatim to other-version branches; pick the right line from the alpha
/// source's comments.
/// </summary>
public sealed class EncryptionManager : Singleton<EncryptionManager>
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const int DwKeySize = 1024;
    private const int AesBlockSize = 16;

    // Keyed by accountId (ulong). The alpha used Dictionary<ulong, ConnectionKeychain>
    // unprotected; we use ConcurrentDictionary because the dispatcher can hit it
    // from multiple receive threads.
    private readonly ConcurrentDictionary<ulong, ConnectionKeychain> _connectionKeys = new();

    public void Load()
    {
        _connectionKeys.Clear();
        Logger.Info("EncryptionManager loaded.");
    }

    private ConnectionKeychain GetConnectionKeys(uint connectionId, ulong accountId)
    {
        if (_connectionKeys.TryGetValue(accountId, out var keys) && keys.ConnectionId == connectionId)
            return keys;
        return GenerateRsaKeyPair(connectionId, accountId);
    }

    private ConnectionKeychain GenerateRsaKeyPair(uint connectionId, ulong accountId)
    {
        _connectionKeys.TryRemove(accountId, out _);
        var rsa = new RSACryptoServiceProvider(DwKeySize);
        var keys = new ConnectionKeychain(connectionId, rsa);
        _connectionKeys[accountId] = keys;
        return keys;
    }

    /// <summary>
    /// Write the server's RSA public modulus + 125 padding bytes + exponent into
    /// the X2EnterWorldResponsePacket pubKey field. Generates a fresh key pair
    /// for the connection.
    /// </summary>
    public PacketStream WritePubKey(uint connectionId, ulong accountId, PacketStream stream)
    {
        var keychain = GenerateRsaKeyPair(connectionId, accountId);
        var parameters = keychain.RsaKeyPair.ExportParameters(false);
        stream.Write(parameters.Modulus);
        stream.Write(new byte[125]);
        stream.Write(parameters.Exponent);
        return stream;
    }

    /// <summary>
    /// Decrypt the client's encrypted AES key and XOR seed (both RSA-encrypted
    /// blocks), apply the 2.0.1.7 mix to the XOR seed to derive the working
    /// XOR key, and store everything on the connection's keychain.
    /// </summary>
    public void StoreClientKeys(byte[] aesKeyEncrypted, byte[] xorKeyEncrypted, ulong accountId, uint connectionId)
    {
        if (!_connectionKeys.TryGetValue(accountId, out var keys))
            return;

        Logger.Debug("StoreClientKeys: AccountId={0} ConnectionId={1}", accountId, connectionId);

        var xorConstRaw = keys.RsaKeyPair.Decrypt(xorKeyEncrypted, false);
        var head = BitConverter.ToUInt32(xorConstRaw, 0);
        // 2.0.1.7-Trion-r249376 mix constants.
        head = ((head ^ 0x15A02442) * head ^ 0x70F1F23) & 0xffffffff;
        keys.XorKey = (head * head) & 0xffffffff;
        keys.AesKey = keys.RsaKeyPair.Decrypt(aesKeyEncrypted, false);
        keys.ReceivedKeys = true;
        Logger.Debug("StoreClientKeys: AES={0} XOR={1}", ByteArrayToHex(keys.AesKey), keys.XorKey);
    }

    public byte GetSCMessageCount(uint connectionId, ulong accountId)
        => GetConnectionKeys(connectionId, accountId).SCMessageCount;

    public void IncSCMsgCount(uint connectionId, ulong accountId)
        => GetConnectionKeys(connectionId, accountId).SCMessageCount++;

    #region S -> C XOR encryption (DD05 packets)

    public byte[] StoCEncrypt(byte[] bodyPacket)
    {
        var length = bodyPacket.Length;
        var array = new byte[length];
        var cry = (uint)(length ^ 0x1F2175A0);
        return ByteXorServer(bodyPacket, array, cry);
    }

    private static byte[] ByteXorServer(byte[] bodyPacket, byte[] array, uint cry)
    {
        var length = bodyPacket.Length;
        var n = 4 * (length / 4);
        for (var i = n - 1; i >= 0; i--)
            array[i] = (byte)(bodyPacket[i] ^ InlineServer(ref cry));
        for (var i = n; i < length; i++)
            array[i] = (byte)(bodyPacket[i] ^ InlineServer(ref cry));
        return array;
    }

    private static byte InlineServer(ref uint cry)
    {
        cry += 0x2FCBD5U;
        var n = (byte)(cry >> 0x10);
        n = (byte)(n & 0xF7);
        return n == 0 ? (byte)0xFE : n;
    }

    /// <summary>
    /// CRC8 used in the Level-5 server-out packet body.
    /// </summary>
    public byte Crc8(byte[] data)
    {
        uint checksum = 0;
        for (var i = 0; i < data.Length; i++)
        {
            checksum *= 0x13;
            checksum += data[i];
        }
        return (byte)checksum;
    }

    #endregion

    #region C -> S decryption (0005 packets)

    public byte[] Decode(byte[] data, uint connectionId, ulong accountId)
    {
        var keys = GetConnectionKeys(connectionId, accountId);
        var ciphertext = DecodeXor(data, keys.XorKey, keys);
        var plaintext = DecodeAes(ciphertext, keys.AesKey, keys.IV);
        keys.CSMessageCount++;
        return plaintext;
    }

    private static byte AddClient(ref uint cry)
    {
        cry += 0x2FCBD5;
        var n = (byte)(cry >> 0x10);
        n = (byte)(n & 0xF7);
        return n == 0 ? (byte)0xFE : n;
    }

    private static byte MakeSeq(ConnectionKeychain keys)
    {
        var seq = keys.CSSecondaryOffsetSequence;
        seq += 0x2FA245;
        var result = (byte)(seq >> 0xE & 0x73);
        if (result == 0)
            result = 0xFE;
        keys.CSSecondaryOffsetSequence = seq;
        return result;
    }

    private static byte[] DecodeXor(byte[] bodyPacket, uint xorKey, ConnectionKeychain keys)
    {
        var seq = keys.CSOffsetSequence;
        var mBodyPacket = new byte[bodyPacket.Length - 3];
        Buffer.BlockCopy(bodyPacket, 3, mBodyPacket, 0, bodyPacket.Length - 3);

        var msgKey = ((uint)(bodyPacket.Length / 16 - 1) << 4) + (uint)(bodyPacket[2] - 47);
        var mul = msgKey * xorKey;
        // 2.0.1.7-Trion-r249376 client mix.
        var cry = mul ^ ((uint)MakeSeq(keys) + 0x75A02458) ^ 0x3458B610;

        var offset = 4;
        if (seq != 0)
        {
            if (seq % 3 != 0)
            {
                if (seq % 5 != 0)
                {
                    if (seq % 7 != 0)
                    {
                        if (seq % 9 != 0)
                        {
                            if (seq % 11 == 0) offset = 7;
                        }
                        else { offset = 3; }
                    }
                    else { offset = 11; }
                }
                else { offset = 2; }
            }
            else { offset = 5; }
        }
        else { offset = 9; }

        var array = new byte[mBodyPacket.Length];
        var n = offset * (mBodyPacket.Length / offset);
        for (var i = n - 1; i >= 0; i--)
            array[i] = (byte)(mBodyPacket[i] ^ AddClient(ref cry));
        for (var i = n; i < mBodyPacket.Length; i++)
            array[i] = (byte)(mBodyPacket[i] ^ AddClient(ref cry));

        keys.CSOffsetSequence += MakeSeq(keys);
        keys.CSOffsetSequence += 1;
        return array;
    }

    private static byte[] DecodeAes(byte[] cipherData, byte[] aesKey, byte[] iv)
    {
        var mIv = new byte[AesBlockSize];
        Buffer.BlockCopy(iv, 0, mIv, 0, AesBlockSize);
        var len = cipherData.Length / AesBlockSize;
        // Save the last 16 bytes back into the connection's IV so the next packet chains.
        Buffer.BlockCopy(cipherData, (len - 1) * AesBlockSize, iv, 0, AesBlockSize);

        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Padding = PaddingMode.None;
        aes.Mode = CipherMode.CBC;
        aes.Key = aesKey;
        aes.IV = mIv;

        using var memoryStream = new MemoryStream();
        using (var cs = new System.Security.Cryptography.CryptoStream(memoryStream, aes.CreateDecryptor(), System.Security.Cryptography.CryptoStreamMode.Write))
        {
            cs.Write(cipherData, 0, cipherData.Length);
            cs.FlushFinalBlock();
        }
        return memoryStream.ToArray();
    }

    #endregion

    private static string ByteArrayToHex(byte[] data)
    {
        const string Lookup = "0123456789ABCDEF";
        var c = new char[data.Length * 2];
        for (int i = 0, p = 0; i < data.Length; i++)
        {
            var b = data[i];
            c[p++] = Lookup[b / 0x10];
            c[p++] = Lookup[b % 0x10];
        }
        return new string(c);
    }
}
