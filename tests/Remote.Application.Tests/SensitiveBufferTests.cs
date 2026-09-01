using Remote.Infrastructure.Security;

namespace Remote.Application.Tests;

public sealed class SensitiveBufferTests
{
    [Fact]
    public void Dispose_PreventsFurtherSecretAccess()
    {
        var buffer = new SensitiveBuffer("secret"u8);

        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffer.Copy());
    }

    [Fact]
    public void Copy_ReturnsIndependentArray()
    {
        using var buffer = new SensitiveBuffer([1, 2, 3]);

        var copy = buffer.Copy();
        copy[0] = 9;

        Assert.Equal([1, 2, 3], buffer.Copy());
    }
}
