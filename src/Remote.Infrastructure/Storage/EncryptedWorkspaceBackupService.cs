namespace Remote.Infrastructure.Storage;

/// <summary>Copies the already-encrypted Workspace without decrypting it.</summary>
public sealed class EncryptedWorkspaceBackupService
{
    public async Task<string> CreateAsync(
        string workspacePath,
        string backupDirectory,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        if (!File.Exists(workspacePath))
        {
            throw new FileNotFoundException("The encrypted Workspace does not exist.", workspacePath);
        }

        Directory.CreateDirectory(backupDirectory);
        var destination = Path.Combine(
            backupDirectory,
            $"workspace-{timestamp:yyyyMMdd-HHmmss-fff}.rmtw.backup");
        var temporary = destination + ".tmp";
        try
        {
            await using (var source = new FileStream(
                workspacePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var target = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task RestoreAsync(
        string backupPath,
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("The encrypted backup does not exist.", backupPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(workspacePath)!);
        var temporary = $"{workspacePath}.{Guid.NewGuid():N}.restore";
        try
        {
            await using (var source = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, workspacePath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
