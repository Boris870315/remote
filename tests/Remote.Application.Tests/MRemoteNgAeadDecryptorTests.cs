using System.Security.Cryptography;
using Remote.Infrastructure.Import;

namespace Remote.Application.Tests;

public sealed class MRemoteNgAeadDecryptorTests
{
    private const string ProtectedMarker =
        "e/T6ajrPtNNlHreSeD4QBqToTuiqtNACKiPJv7vU+l6TWCu9JNsmL+Y8lJ4aTl5YVcstXpQjxsZ9i8+YV4Gs";

    [Fact]
    public void Decrypt_AuthenticatesKnownMRemoteNg26Marker()
    {
        var result = new MRemoteNgAeadDecryptor().Decrypt(ProtectedMarker, "Password", 1000);

        Assert.Equal("ThisIsProtected", result);
    }

    [Fact]
    public void Decrypt_RejectsIncorrectPassword()
    {
        Assert.Throws<CryptographicException>(() =>
            new MRemoteNgAeadDecryptor().Decrypt(ProtectedMarker, "wrong", 1000));
    }
}
