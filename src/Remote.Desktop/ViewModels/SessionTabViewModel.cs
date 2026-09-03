using CommunityToolkit.Mvvm.ComponentModel;
using Remote.Application.Sessions;
using Remote.Application.Connections;

namespace Remote.Desktop.ViewModels;

public sealed partial class SessionTabViewModel(
    SessionId sessionId,
    ConnectionProfile connection) : ViewModelBase
{
    public SessionId SessionId { get; } = sessionId;
    public ConnectionProfile Connection { get; } = connection;
    public string ConnectionName => Connection.Name;
    public string ProtocolLabel => Connection.ProtocolId.ToUpperInvariant();

    [ObservableProperty]
    private string stateLabel = "連線中";

    [ObservableProperty]
    private bool isSelected;
}
