using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Remote.Infrastructure.Vaults;

/// <summary>Protects the Workspace master password with a separately issued recovery key.</summary>
public sealed class RecoveryKeyEnvelopeService
{
    private const int Version = 1;
    private const int Iterations = 600_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> CreateAsync(
        string path,
        string masterPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterPassword);
        var random = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = new byte[32];
        var plaintext = Encoding.UTF8.GetBytes(masterPassword);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        try
        {
            var recoveryKey = Base64Url(random);
            Rfc2898DeriveBytes.Pbkdf2(Normalize(recoveryKey), salt, key, Iterations, HashAlgorithmName.SHA256);
            using (var aes = new AesGcm(key, tag.Length))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag);
            }
            var envelope = JsonSerializer.SerializeToUtf8Bytes(
                new Envelope(Version, Iterations, salt, nonce, ciphertext, tag), JsonOptions);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, envelope, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            return GroupForDisplay(recoveryKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    public async Task<string> RecoverMasterPasswordAsync(
        string path,
        string recoveryKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryKey);
        byte[] json;
        try
        {
            json = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidDataException("尚未建立此 Workspace 的 Recovery Key。", exception);
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonOptions)
                ?? throw new InvalidDataException("Recovery Key 資料已損壞。");
            if (envelope.Version != Version || envelope.Iterations < 100_000)
            {
                throw new InvalidDataException("不支援此 Recovery Key 格式。");
            }
            var key = new byte[32];
            var plaintext = new byte[envelope.Ciphertext.Length];
            try
            {
                Rfc2898DeriveBytes.Pbkdf2(Normalize(recoveryKey), envelope.Salt, key, envelope.Iterations, HashAlgorithmName.SHA256);
                using var aes = new AesGcm(key, envelope.Tag.Length);
                aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException("Recovery Key 不正確或資料已損壞。", exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Recovery Key 資料已損壞。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private static string Normalize(string value) => value.Replace(".", string.Empty, StringComparison.Ordinal)
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .Trim();

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=')
        .Replace('+', '-').Replace('/', '_');

    private static string GroupForDisplay(string value) => string.Join('.',
        Enumerable.Range(0, (value.Length + 4) / 5).Select(index => value.Substring(index * 5, Math.Min(5, value.Length - index * 5))));

    private sealed record Envelope(int Version, int Iterations, byte[] Salt, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
}
