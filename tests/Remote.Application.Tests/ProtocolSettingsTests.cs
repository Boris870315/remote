using Remote.Application.Connections;

namespace Remote.Application.Tests;

public sealed class ProtocolSettingsTests
{
    [Theory]
    [InlineData("rdp.password")]
    [InlineData("http.apiToken")]
    [InlineData("ssh.privateKey")]
    [InlineData("credential.secret")]
    public void Set_WhenKeyCouldContainSecret_RejectsValue(string key)
    {
        var settings = new ProtocolSettings();

        var exception = Assert.Throws<ArgumentException>(() => settings.Set(key, "sensitive"));

        Assert.Contains("Credential reference", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_ReturnsNewImmutableSettings()
    {
        var original = new ProtocolSettings();

        var updated = original.Set("rdp.admin", "True");

        Assert.Null(original.Get("rdp.admin"));
        Assert.True(updated.GetBoolean("rdp.admin"));
    }
}
