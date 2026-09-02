using Remote.Infrastructure.Vaults;

namespace Remote.Application.Tests;

public sealed class RecoveryKeyEnvelopeServiceTests
{
    [Fact]
    public async Task CreatedKey_RecoversMasterPasswordAfterServiceRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"remote-recovery-{Guid.NewGuid():N}.json");
        try
        {
            var key = await new RecoveryKeyEnvelopeService().CreateAsync(path, "correct horse battery staple");

            var recovered = await new RecoveryKeyEnvelopeService().RecoverMasterPasswordAsync(path, key);

            Assert.Equal("correct horse battery staple", recovered);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WrongKey_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"remote-recovery-{Guid.NewGuid():N}.json");
        try
        {
            await new RecoveryKeyEnvelopeService().CreateAsync(path, "master-password");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new RecoveryKeyEnvelopeService().RecoverMasterPasswordAsync(path, "wrong-key"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
