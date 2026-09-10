using CommunityToolkit.Mvvm.ComponentModel;
using Remote.Application.Sessions;
using Remote.Application.Connections;
using Remote.Protocols;

namespace Remote.Desktop.ViewModels;

public sealed partial class SessionTabViewModel(
    SessionId sessionId,
    ConnectionProfile connection,
    string? displayName = null) : ViewModelBase
{
    public SessionId SessionId { get; } = sessionId;
    public ConnectionProfile Connection { get; private set; } = connection;
    public string ConnectionName { get; } = displayName ?? connection.Name;
    public string ConnectionDetail => $"{Connection.Endpoint.Host}:{GetPort(Connection)}";
    public string ProtocolLabel => Connection.ProtocolId.ToUpperInvariant();

    [ObservableProperty]
    private string stateLabel = "連線中";

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isViewOnly = connection.DefaultAccessMode is SessionAccessMode.ViewOnly;

    public void SetViewOnly(bool value)
    {
        IsViewOnly = value;
        Connection = Connection with
        {
            DefaultAccessMode = value ? SessionAccessMode.ViewOnly : SessionAccessMode.Interactive,
        };
    }

    private static int GetPort(ConnectionProfile connection) => connection.Endpoint.Port >= 0
        ? connection.Endpoint.Port
        : connection.ProtocolId.ToLowerInvariant() switch
        {
            "rdp" => 3389,
            "vnc" => 5900,
            "ssh2" => 22,
            "http" => 80,
            "https" => 443,
            _ => 0,
        };
}
