using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Remote.Infrastructure.Import;

/// <summary>Decrypts the AES/GCM field format used by mRemoteNG 1.75 / ConfVersion 2.6.</summary>
public sealed class MRemoteNgAeadDecryptor
{
    private const int SaltSize = 16;
    private const int NonceSize = 16;
    private const int TagBits = 128;

    public string Decrypt(string value, string password, int iterations)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (iterations < 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        var encrypted = Convert.FromBase64String(value);
        if (encrypted.Length <= SaltSize + NonceSize + TagBits / 8)
        {
            throw new InvalidDataException("The mRemoteNG encrypted value is incomplete.");
        }

        var salt = encrypted.AsSpan(0, SaltSize).ToArray();
        var nonce = encrypted.AsSpan(SaltSize, NonceSize).ToArray();
        var ciphertext = encrypted.AsSpan(SaltSize + NonceSize).ToArray();
        var passwordBytes = ToPkcs5Bytes(password);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            passwordBytes,
            salt,
            iterations,
            HashAlgorithmName.SHA1,
            32);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(false, new AeadParameters(new KeyParameter(key), TagBits, nonce, salt));
            var written = cipher.ProcessBytes(ciphertext, 0, ciphertext.Length, plaintext, 0);
            written += cipher.DoFinal(plaintext, written);
            return Encoding.UTF8.GetString(plaintext, 0, written);
        }
        catch (InvalidCipherTextException exception)
        {
            throw new CryptographicException("The mRemoteNG import password is incorrect or the data is damaged.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static byte[] ToPkcs5Bytes(string password)
    {
        var bytes = new byte[password.Length];
        for (var index = 0; index < password.Length; index++)
        {
            bytes[index] = unchecked((byte)password[index]);
        }

        return bytes;
    }
}
