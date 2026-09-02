using Remote.Infrastructure.Storage;

namespace Remote.Application.Tests;

public sealed class EncryptedWorkspaceBackupServiceTests
{
    [Fact]
    public async Task CreateAsync_CopiesEncryptedBytesAndUsesTimestampedName()
    {
        var root = Path.Combine(Path.GetTempPath(), $"remote-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = Path.Combine(root, "workspace.rmtw");
            var bytes = new byte[] { 0x52, 0x4D, 0x54, 0x57, 0x01, 0xFE };
            await File.WriteAllBytesAsync(workspace, bytes);

            var destination = await new EncryptedWorkspaceBackupService().CreateAsync(
                workspace,
                Path.Combine(root, "Backups"),
                new DateTimeOffset(2026, 9, 2, 3, 4, 5, 678, TimeSpan.Zero));

            Assert.EndsWith("workspace-20260902-030405-678.rmtw.backup", destination);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_ReplacesWorkspaceWithSelectedEncryptedBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"remote-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = Path.Combine(root, "workspace.rmtw");
            var backup = Path.Combine(root, "saved.rmtw.backup");
            await File.WriteAllTextAsync(workspace, "current");
            await File.WriteAllTextAsync(backup, "restored");

            await new EncryptedWorkspaceBackupService().RestoreAsync(backup, workspace);

            Assert.Equal("restored", await File.ReadAllTextAsync(workspace));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"remote-backup-{Guid.NewGuid():N}");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new EncryptedWorkspaceBackupService().CreateAsync(
                Path.Combine(root, "missing.rmtw"),
                Path.Combine(root, "Backups"),
                DateTimeOffset.UtcNow));
    }
}
