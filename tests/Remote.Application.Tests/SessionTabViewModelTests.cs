using Remote.Application.Connections;
using Remote.Application.Sessions;
using Remote.Desktop.ViewModels;

namespace Remote.Application.Tests;

public sealed class SessionTabViewModelTests
{
    [Fact]
    public void ConnectionName_UsesDistinctDisplayNameWhenProvided()
    {
        var connection = new ConnectionProfile
        {
            Id = ConnectionId.New(),
            Name = "Windows Prod",
            ProtocolId = "rdp",
            Endpoint = new Uri("rdp://10.20.0.24"),
        };

        var tab = new SessionTabViewModel(SessionId.New(), connection, "Windows Prod · 2");

        Assert.Equal("Windows Prod · 2", tab.ConnectionName);
        Assert.Equal("10.20.0.24:3389", tab.ConnectionDetail);
    }
}
