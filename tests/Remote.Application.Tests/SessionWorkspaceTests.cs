using Remote.Application.Connections;
using Remote.Application.Sessions;
using Remote.Protocols;

namespace Remote.Application.Tests;

public sealed class SessionWorkspaceTests
{
    [Fact]
    public void RequestOpen_WhenConnectionAlreadyHasOpenSession_RequiresChoice()
    {
        var workspace = new SessionWorkspace();
        var connection = CreateConnection();
        workspace.OpenNew(connection);

        var request = workspace.RequestOpen(connection);

        Assert.True(request.RequiresChoice);
        Assert.Single(request.ExistingSessions);
    }

    [Fact]
    public void SetState_FollowsConnectionLifecycle()
    {
        var workspace = new SessionWorkspace();
        var session = workspace.OpenNew(CreateConnection());

        var connected = workspace.SetState(session.Id, SessionState.Connected);
        var disconnecting = workspace.SetState(session.Id, SessionState.Disconnecting);
        var disconnected = workspace.SetState(session.Id, SessionState.Disconnected);

        Assert.Equal(SessionState.Connected, connected.State);
        Assert.Equal(SessionState.Disconnecting, disconnecting.State);
        Assert.Equal(SessionState.Disconnected, disconnected.State);
    }

    [Fact]
    public void RequestOpen_AfterExternalClientHandoff_AllowsRetry()
    {
        var workspace = new SessionWorkspace();
        var connection = CreateConnection();
        var session = workspace.OpenNew(connection);
        workspace.SetState(session.Id, SessionState.ExternalClientLaunched);

        var request = workspace.RequestOpen(connection);

        Assert.False(request.RequiresChoice);
        Assert.Empty(request.ExistingSessions);
    }

    [Fact]
    public void SetState_WhenTransitionIsInvalid_RejectsChange()
    {
        var workspace = new SessionWorkspace();
        var session = workspace.OpenNew(CreateConnection());

        Assert.Throws<InvalidOperationException>(
            () => workspace.SetState(session.Id, SessionState.Disconnected));
    }

    private static ConnectionProfile CreateConnection() => new()
    {
        Id = ConnectionId.New(),
        Name = "Windows Prod",
        Endpoint = new Uri("rdp://10.20.0.24"),
        ProtocolId = "rdp",
        DefaultAccessMode = SessionAccessMode.ViewOnly,
    };
}
