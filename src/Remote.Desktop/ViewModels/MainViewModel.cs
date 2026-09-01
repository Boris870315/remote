using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remote.Application;
using Remote.Application.Connections;
using Remote.Application.Sessions;
using Remote.Protocols;

namespace Remote.Desktop.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IRemoteSessionService _sessionService;
    private readonly IRemoteProtocol _protocol;
    private readonly SessionLaunchPolicy _launchPolicy;
    private readonly SessionWorkspace _sessionWorkspace = new();

    public MainViewModel()
        : this(new RemoteSessionService(), new RemoteDesktopProtocol(), new SessionLaunchPolicy())
    {
    }

    public MainViewModel(
        IRemoteSessionService sessionService,
        IRemoteProtocol protocol,
        SessionLaunchPolicy launchPolicy)
    {
        _sessionService = sessionService;
        _protocol = protocol;
        _launchPolicy = launchPolicy;
        var windowsProd = CreateConnection("Windows Prod", "rdp", "rdp://10.20.0.24:3389");
        var designMac = CreateConnection("Design Mac", "vnc", "vnc://10.20.0.31:5900");
        var labSsh = CreateConnection("Lab SSH", "ssh2", "ssh://10.20.1.18:22");
        var financeVm = CreateConnection("Finance VM", "rdp", "rdp://10.20.2.12:3389");
        Connections =
        [
            new(windowsProd, "RDP only", true),
            new(designMac, "VNC only", false),
            new(labSsh, "SSH2 only", false),
            new(financeVm, "RDP only", false),
        ];
        selectedConnection = Connections[0];
        UpdateAccessMode();
    }

    public string Title => "Remote";

    public ObservableCollection<ConnectionListItem> Connections { get; }

    [ObservableProperty]
    private ConnectionListItem? selectedConnection;

    [ObservableProperty]
    private bool isViewOnly;

    [ObservableProperty]
    private string accessModeLabel = "Interactive";

    [ObservableProperty]
    private string statusDetail = "Ready";

    [ObservableProperty]
    private string sessionStatusLabel = "尚未開啟工作階段";

    public string RuntimeStatus => _sessionService.GetStatus().State;

    public string ProtocolName => _protocol.Descriptor.DisplayName;

    partial void OnIsViewOnlyChanged(bool value) => UpdateAccessMode();

    [RelayCommand]
    private void OpenSelectedSession()
    {
        if (SelectedConnection is null)
        {
            return;
        }

        var request = _sessionWorkspace.RequestOpen(SelectedConnection.Profile);
        if (request.RequiresChoice)
        {
            SessionStatusLabel = "已有工作階段：請選擇切換既有分頁或另開工作階段";
            return;
        }

        var session = _sessionWorkspace.OpenNew(SelectedConnection.Profile);
        SessionStatusLabel = $"{session.State} · 等待 {SelectedConnection.Protocol} Adapter Host";
    }

    private void UpdateAccessMode()
    {
        var mode = IsViewOnly ? SessionAccessMode.ViewOnly : SessionAccessMode.Interactive;
        var result = _launchPolicy.Validate(_protocol, mode);
        AccessModeLabel = IsViewOnly ? "VIEW ONLY" : "INTERACTIVE";
        StatusDetail = result.Detail;
    }

    private static ConnectionProfile CreateConnection(string name, string protocolId, string endpoint) => new()
    {
        Id = ConnectionId.New(),
        Name = name,
        ProtocolId = protocolId,
        Endpoint = new Uri(endpoint),
    };
}

public sealed record ConnectionListItem(
    ConnectionProfile Profile,
    string IdentityScope,
    bool IsFavorite)
{
    public string Name => Profile.Name;

    public string Protocol => Profile.ProtocolId.ToUpperInvariant();

    public string Endpoint => $"{Profile.Endpoint.Host}:{Profile.Endpoint.Port}";
}
