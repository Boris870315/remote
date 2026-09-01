using Remote.Infrastructure.Protocols.Ssh;

namespace Remote.Application.Tests;

public sealed class SshHostKeyPolicyTests
{
    private static readonly SshHostKeyInfo Presented = new("ssh-ed25519", "abc123");

    [Theory]
    [InlineData("abc123")]
    [InlineData("SHA256:abc123")]
    public void KnownMatchingFingerprint_IsTrusted(string expected) =>
        Assert.True(SshHostKeyPolicy.CanTrust(Presented, expected, null));

    [Fact]
    public void KnownMismatchedFingerprint_CannotBeOverriddenByPrompt() =>
        Assert.False(SshHostKeyPolicy.CanTrust(Presented, "different", _ => true));

    [Fact]
    public void UnknownFingerprint_RequiresExplicitConfirmation()
    {
        Assert.False(SshHostKeyPolicy.CanTrust(Presented, null, null));
        Assert.True(SshHostKeyPolicy.CanTrust(Presented, null, key => key == Presented));
    }
}
