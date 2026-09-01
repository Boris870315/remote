using System.Text;
using Remote.Infrastructure.Security;
using Remote.Infrastructure.Storage;

namespace Remote.Application.Tests;

public sealed class WorkspaceCryptographyTests
{
    private static readonly WorkspaceEncryptionOptions FastTestOptions = new()
    {
        Pbkdf2Iterations = 10_000,
    };

    [Fact]
    public void Encrypt_ThenDecrypt_RoundTripsPayloadWithoutPlaintextLeak()
    {
        var cryptography = new WorkspaceCryptography(FastTestOptions);
        var plaintext = "credential-password-value"u8.ToArray();

        var encrypted = cryptography.Encrypt(plaintext, "correct horse battery staple");
        var decrypted = cryptography.Decrypt(encrypted, "correct horse battery staple");

        Assert.Equal(plaintext, decrypted);
        Assert.DoesNotContain("credential-password-value", Encoding.UTF8.GetString(encrypted));
    }

    [Fact]
    public void Decrypt_WithWrongPassword_RejectsWorkspace()
    {
        var cryptography = new WorkspaceCryptography(FastTestOptions);
        var encrypted = cryptography.Encrypt("workspace"u8, "correct password");

        Assert.Throws<WorkspaceUnlockException>(
            () => cryptography.Decrypt(encrypted, "wrong password"));
    }

    [Fact]
    public async Task FileStore_WriteThenRead_AtomicallyRestoresPayload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"remote-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "workspace.remote");
        try
        {
            var store = new EncryptedWorkspaceFile(new WorkspaceCryptography(FastTestOptions));

            await store.WriteAsync(path, "workspace payload"u8.ToArray(), "master password");
            var restored = await store.ReadAsync(path, "master password");

            Assert.Equal("workspace payload", Encoding.UTF8.GetString(restored));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
