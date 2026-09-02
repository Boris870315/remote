using CommunityToolkit.Mvvm.ComponentModel;
using Remote.Application.Sessions;

namespace Remote.Desktop.ViewModels;

public sealed partial class SessionTabViewModel(
    SessionId sessionId,
    string connectionName,
    string protocolId) : ViewModelBase
{
    public SessionId SessionId { get; } = sessionId;
    public string ConnectionName { get; } = connectionName;
    public string ProtocolLabel { get; } = protocolId.ToUpperInvariant();

    [ObservableProperty]
    private string stateLabel = "連線中";

    [ObservableProperty]
    private bool isSelected;
}
