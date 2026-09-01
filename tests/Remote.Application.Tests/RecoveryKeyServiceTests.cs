using Remote.Infrastructure.Vaults;

namespace Remote.Application.Tests;

public sealed class RecoveryKeyServiceTests
{
    [Fact]
    public void Rotate_IssuesVerifiableHighEntropyKey()
    {
        using var service = new RecoveryKeyService();

        var recoveryKey = service.Rotate();

        Assert.True(service.Verify(recoveryKey));
        Assert.True(recoveryKey.Count(character => character != '.') >= 43);
    }

    [Fact]
    public void Rotate_InvalidatesPreviouslyIssuedKey()
    {
        using var service = new RecoveryKeyService();
        var oldKey = service.Rotate();

        var newKey = service.Rotate();

        Assert.False(service.Verify(oldKey));
        Assert.True(service.Verify(newKey));
    }

    [Fact]
    public void Verify_AcceptsKeyWithoutDisplaySeparators()
    {
        using var service = new RecoveryKeyService();
        var recoveryKey = service.Rotate();

        Assert.True(service.Verify(recoveryKey.Replace(".", string.Empty, StringComparison.Ordinal)));
    }
}
