using Remote.Application.Connections;

namespace Remote.Application.Tests;

public sealed class WebNavigationPolicyTests
{
    [Fact]
    public void HostWithoutScheme_DefaultsToHttps() =>
        Assert.Equal("https://example.com/", WebNavigationPolicy.ParseHttpEndpoint("example.com").ToString());

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ssh://example.com")]
    [InlineData("https://user:secret@example.com")]
    public void NonWebOrCredentialBearingAddress_IsRejected(string value) =>
        Assert.Throws<ArgumentException>(() => WebNavigationPolicy.ParseHttpEndpoint(value));
}
