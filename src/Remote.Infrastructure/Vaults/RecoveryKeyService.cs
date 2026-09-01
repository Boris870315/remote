using System.Security.Cryptography;
using System.Text;

namespace Remote.Infrastructure.Vaults;

/// <summary>Issues and verifies a high-entropy recovery key without retaining its plaintext.</summary>
public sealed class RecoveryKeyService : IDisposable
{
    private byte[]? _recoveryKeyHash;

    public string Rotate()
    {
        var random = RandomNumberGenerator.GetBytes(32);
        try
        {
            var recoveryKey = Convert.ToBase64String(random)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            ReplaceHash(Hash(recoveryKey));
            return GroupForDisplay(recoveryKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
        }
    }

    public bool Verify(ReadOnlySpan<char> recoveryKey)
    {
        if (_recoveryKeyHash is null || recoveryKey.IsEmpty)
        {
            return false;
        }

        var normalized = Normalize(recoveryKey);
        var candidateHash = Hash(normalized);
        try
        {
            return CryptographicOperations.FixedTimeEquals(_recoveryKeyHash, candidateHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidateHash);
        }
    }

    public void Dispose() => ReplaceHash(null);

    private void ReplaceHash(byte[]? replacement)
    {
        if (_recoveryKeyHash is not null)
        {
            CryptographicOperations.ZeroMemory(_recoveryKeyHash);
        }

        _recoveryKeyHash = replacement;
    }

    private static byte[] Hash(ReadOnlySpan<char> value)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(Encoding.UTF8.GetMaxByteCount(value.Length));
        try
        {
            var written = Encoding.UTF8.GetBytes(value, bytes);
            return SHA256.HashData(bytes.AsSpan(0, written));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Normalize(ReadOnlySpan<char> value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is not '.' and not ' ')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string GroupForDisplay(string recoveryKey)
    {
        var builder = new StringBuilder(recoveryKey.Length + recoveryKey.Length / 5);
        for (var index = 0; index < recoveryKey.Length; index++)
        {
            if (index > 0 && index % 5 == 0)
            {
                builder.Append('.');
            }

            builder.Append(recoveryKey[index]);
        }

        return builder.ToString();
    }
}
