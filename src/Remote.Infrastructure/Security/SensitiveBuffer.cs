using System.Security.Cryptography;

namespace Remote.Infrastructure.Security;

/// <summary>Owns sensitive bytes and clears its backing memory when disposed.</summary>
public sealed class SensitiveBuffer : IDisposable
{
    private byte[]? _bytes;

    public SensitiveBuffer(ReadOnlySpan<byte> value)
    {
        _bytes = value.ToArray();
    }

    public int Length => GetBytes().Length;

    public byte[] Copy() => GetBytes().ToArray();

    public SensitiveBuffer Clone() => new(GetBytes());

    public void Dispose()
    {
        if (_bytes is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_bytes);
        _bytes = null;
    }

    private ReadOnlySpan<byte> GetBytes() => _bytes is null
        ? throw new ObjectDisposedException(nameof(SensitiveBuffer))
        : _bytes;
}
