using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Remote.Infrastructure.Protocols.Vnc;

internal static class VncAuthentication
{
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The legacy VNC Authentication wire protocol mandates DES; transport security is handled separately.")]
    public static byte[] EncryptChallenge(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> password)
    {
        if (challenge.Length != 16)
        {
            throw new ArgumentException("A VNC challenge must contain 16 bytes.", nameof(challenge));
        }

        Span<byte> key = stackalloc byte[8];
        password[..Math.Min(password.Length, key.Length)].CopyTo(key);
        for (var index = 0; index < key.Length; index++)
        {
            key[index] = ReverseBits(key[index]);
        }

        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        var keyBytes = key.ToArray();
        var challengeBytes = challenge.ToArray();
        try
        {
            des.Key = keyBytes;
            using var encryptor = des.CreateEncryptor();
            return encryptor.TransformFinalBlock(challengeBytes, 0, challengeBytes.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(challengeBytes);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte ReverseBits(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4) | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2));
        return (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1));
    }
}
