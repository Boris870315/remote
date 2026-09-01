using Remote.Infrastructure.Security;

namespace Remote.Infrastructure.Storage;

/// <summary>Atomically reads and replaces a complete encrypted Workspace file.</summary>
public sealed class EncryptedWorkspaceFile(WorkspaceCryptography cryptography)
{
    public async Task WriteAsync(
        string path,
        ReadOnlyMemory<byte> plaintext,
        string masterPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrEmpty(masterPassword);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("Workspace path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var encrypted = cryptography.Encrypt(plaintext.Span, masterPassword.AsSpan());
        var temporaryPath = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, encrypted, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<byte[]> ReadAsync(
        string path,
        string masterPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrEmpty(masterPassword);
        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return cryptography.Decrypt(encrypted, masterPassword.AsSpan());
    }
}
