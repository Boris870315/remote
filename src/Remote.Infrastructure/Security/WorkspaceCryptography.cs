using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Remote.Infrastructure.Security;

/// <summary>Encrypts complete Workspace payloads in a versioned authenticated envelope.</summary>
public sealed class WorkspaceCryptography(WorkspaceEncryptionOptions? options = null)
{
    private static ReadOnlySpan<byte> Magic => "RMTW"u8;
    private readonly WorkspaceEncryptionOptions _options = options ?? new();

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<char> masterPassword)
    {
        ValidatePassword(masterPassword);
        var salt = RandomNumberGenerator.GetBytes(_options.SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(_options.NonceSize);
        var key = DeriveKey(masterPassword, salt, _options.Pbkdf2Iterations, _options.KeySize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[_options.TagSize];

        try
        {
            using var aes = new AesGcm(key, _options.TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, BuildAssociatedData(_options.Pbkdf2Iterations));
            return BuildEnvelope(salt, nonce, tag, ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Decrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<char> masterPassword)
    {
        ValidatePassword(masterPassword);
        var headerSize = Magic.Length + sizeof(int) + sizeof(int) + 3;
        if (envelope.Length < headerSize)
        {
            throw new InvalidDataException("Workspace file is incomplete.");
        }

        if (!envelope[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Workspace file signature is invalid.");
        }

        var offset = Magic.Length;
        var version = BinaryPrimitives.ReadInt32LittleEndian(envelope[offset..]);
        offset += sizeof(int);
        if (version != WorkspaceEncryptionOptions.CurrentFormatVersion)
        {
            throw new NotSupportedException($"Workspace format version {version} is not supported.");
        }

        var iterations = BinaryPrimitives.ReadInt32LittleEndian(envelope[offset..]);
        offset += sizeof(int);
        var saltSize = envelope[offset++];
        var nonceSize = envelope[offset++];
        var tagSize = envelope[offset++];
        var requiredSize = offset + saltSize + nonceSize + tagSize;
        if (iterations <= 0 || saltSize <= 0 || nonceSize <= 0 || tagSize <= 0 || envelope.Length < requiredSize)
        {
            throw new InvalidDataException("Workspace encryption header is invalid.");
        }

        var salt = envelope.Slice(offset, saltSize);
        offset += saltSize;
        var nonce = envelope.Slice(offset, nonceSize);
        offset += nonceSize;
        var tag = envelope.Slice(offset, tagSize);
        offset += tagSize;
        var ciphertext = envelope[offset..];
        var plaintext = new byte[ciphertext.Length];
        var key = DeriveKey(masterPassword, salt, iterations, _options.KeySize);

        try
        {
            using var aes = new AesGcm(key, tagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, BuildAssociatedData(iterations));
            return plaintext;
        }
        catch (AuthenticationTagMismatchException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new WorkspaceUnlockException("The master password is incorrect or the Workspace is damaged.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] BuildEnvelope(byte[] salt, byte[] nonce, byte[] tag, byte[] ciphertext)
    {
        var envelope = new byte[Magic.Length + sizeof(int) + sizeof(int) + 3 + salt.Length + nonce.Length + tag.Length + ciphertext.Length];
        var offset = 0;
        Magic.CopyTo(envelope);
        offset += Magic.Length;
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(offset), WorkspaceEncryptionOptions.CurrentFormatVersion);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(offset), _options.Pbkdf2Iterations);
        offset += sizeof(int);
        envelope[offset++] = checked((byte)salt.Length);
        envelope[offset++] = checked((byte)nonce.Length);
        envelope[offset++] = checked((byte)tag.Length);
        salt.CopyTo(envelope, offset);
        offset += salt.Length;
        nonce.CopyTo(envelope, offset);
        offset += nonce.Length;
        tag.CopyTo(envelope, offset);
        offset += tag.Length;
        ciphertext.CopyTo(envelope, offset);
        return envelope;
    }

    private static byte[] DeriveKey(
        ReadOnlySpan<char> password,
        ReadOnlySpan<byte> salt,
        int iterations,
        int keySize)
    {
        var passwordBytes = GC.AllocateUninitializedArray<byte>(Encoding.UTF8.GetMaxByteCount(password.Length));
        try
        {
            var written = Encoding.UTF8.GetBytes(password, passwordBytes);
            return Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes.AsSpan(0, written),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                keySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static byte[] BuildAssociatedData(int iterations)
    {
        var data = new byte[Magic.Length + sizeof(int) + sizeof(int)];
        Magic.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(Magic.Length), WorkspaceEncryptionOptions.CurrentFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(Magic.Length + sizeof(int)), iterations);
        return data;
    }

    private static void ValidatePassword(ReadOnlySpan<char> masterPassword)
    {
        if (masterPassword.IsEmpty)
        {
            throw new ArgumentException("A master password is required.", nameof(masterPassword));
        }
    }
}

public sealed class WorkspaceUnlockException(string message, Exception innerException)
    : CryptographicException(message, innerException);
