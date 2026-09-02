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
}
