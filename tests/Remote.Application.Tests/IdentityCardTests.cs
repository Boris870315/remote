using Remote.Application.Connections;
using Remote.Application.Credentials;

namespace Remote.Application.Tests;

public sealed class IdentityCardTests
{
    [Fact]
    public void CanBeUsedBy_WhenProtocolDiffers_ReturnsFalse()
    {
        var card = new IdentityCard
        {
            VaultId = new VaultId(Guid.NewGuid()),
            CredentialId = new CredentialId(Guid.NewGuid()),
            Name = "Windows Admin",
            ProtocolId = "rdp",
            Username = "operator",
        };
        var connection = new ConnectionProfile
        {
            Id = ConnectionId.New(),
            Name = "Design Mac",
            Endpoint = new Uri("vnc://10.20.0.31"),
            ProtocolId = "vnc",
        };

        Assert.False(card.CanBeUsedBy(connection));
    }
}
