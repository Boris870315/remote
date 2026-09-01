using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media.Imaging;
using Remote.Application;
using Remote.Application.Connections;
using Remote.Application.Sessions;
using Remote.Infrastructure.Processes;
using Remote.Infrastructure.Protocols.Rdp;
using Remote.Infrastructure.Protocols.Vnc;
using Remote.Desktop.Protocols.Vnc;
using Remote.Protocols;

namespace Remote.Desktop.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IRemoteSessionService _sessionService;
    private readonly IRemoteProtocol _protocol;
    private readonly SessionLaunchPolicy _launchPolicy;
    private readonly SessionWorkspace _sessionWorkspace = new();
    private readonly RdpExternalSessionLauncher _rdpLauncher;
    private RfbClient? _vncClient;
    private AvaloniaRfbFrameSink? _vncFrameSink;
    private CancellationTokenSource? _vncCancellation;

    public MainViewModel()
        : this(
            new RemoteSessionService(),
            new RemoteDesktopProtocol(),
            new SessionLaunchPolicy(),
            new RdpExternalSessionLauncher(
                new SystemProcessLauncher(),
                new WindowsRdpLaunchSpecFactory(),
                new MacOsRdpLaunchSpecFactory()))
    {
    }

    public MainViewModel(
        IRemoteSessionService sessionService,
        IRemoteProtocol protocol,
        SessionLaunchPolicy launchPolicy,
        RdpExternalSessionLauncher rdpLauncher)
    {
        _sessionService = sessionService;
        _protocol = protocol;
        _launchPolicy = launchPolicy;
        _rdpLauncher = rdpLauncher;
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

    [ObservableProperty]
    private string quickConnectText = string.Empty;

    [ObservableProperty]
    private bool isEditingConnection;

    [ObservableProperty]
    private string editName = string.Empty;

    [ObservableProperty]
    private string editHost = string.Empty;

    [ObservableProperty]
    private string editPort = "3389";

    [ObservableProperty]
    private string editGateway = string.Empty;

    [ObservableProperty]
    private bool editUseAllMonitors;

    [ObservableProperty]
    private bool editViewOnly;

    [ObservableProperty]
    private bool editRedirectClipboard = true;

    [ObservableProperty]
    private bool editRedirectPrinters;

    [ObservableProperty]
    private bool editRedirectDrives;

    [ObservableProperty]
    private string connectionEditorError = string.Empty;

    [ObservableProperty]
    private WriteableBitmap? remoteFrame;

    public bool IsVncSessionActive => _vncClient?.IsConnected is true;

    public Task<bool> SendVncKeyAsync(uint keySym, bool isDown) =>
        _vncClient?.SendKeyAsync(keySym, isDown) ?? Task.FromResult(false);

    public Task<bool> SendVncPointerAsync(byte buttonMask, ushort x, ushort y) =>
        _vncClient?.SendPointerAsync(buttonMask, x, y) ?? Task.FromResult(false);

    public string RuntimeStatus => _sessionService.GetStatus().State;

    public string ProtocolName => _protocol.Descriptor.DisplayName;

    partial void OnIsViewOnlyChanged(bool value) => UpdateAccessMode();

    [RelayCommand]
    private void BeginNewConnection()
    {
        EditName = string.Empty;
        EditHost = string.Empty;
        EditPort = "3389";
        EditGateway = string.Empty;
        EditUseAllMonitors = false;
        EditViewOnly = false;
        EditRedirectClipboard = true;
        EditRedirectPrinters = false;
        EditRedirectDrives = false;
        ConnectionEditorError = string.Empty;
        IsEditingConnection = true;
    }

    [RelayCommand]
    private void CancelConnectionEdit()
    {
        ConnectionEditorError = string.Empty;
        IsEditingConnection = false;
    }

    [RelayCommand]
    private void SaveConnection()
    {
        var name = EditName.Trim();
        var host = EditHost.Trim();
        if (name.Length == 0 || host.Length == 0)
        {
            ConnectionEditorError = "名稱與主機為必填欄位";
            return;
        }

        if (!int.TryParse(EditPort, out var port) || port is < 1 or > 65535)
        {
            ConnectionEditorError = "連接埠必須介於 1 到 65535";
            return;
        }

        if (Uri.CheckHostName(host) is UriHostNameType.Unknown)
        {
            ConnectionEditorError = "主機名稱或 IP 位址格式無效";
            return;
        }

        var settings = new RdpConnectionSettings
        {
            GatewayHost = string.IsNullOrWhiteSpace(EditGateway) ? null : EditGateway.Trim(),
            RedirectClipboard = EditRedirectClipboard,
            RedirectPrinters = EditRedirectPrinters,
            RedirectDrives = EditRedirectDrives,
        };
        try
        {
            settings.Validate();
        }
        catch (ArgumentException exception)
        {
            ConnectionEditorError = exception.Message;
            return;
        }

        var endpoint = new UriBuilder("rdp", host, port).Uri;
        var profile = new ConnectionProfile
        {
            Id = ConnectionId.New(),
            Name = name,
            Endpoint = endpoint,
            ProtocolId = "rdp",
            DefaultAccessMode = EditViewOnly ? SessionAccessMode.ViewOnly : SessionAccessMode.Interactive,
            Display = new DisplayPreferences
            {
                MonitorSelection = EditUseAllMonitors ? MonitorSelection.All : MonitorSelection.Single,
            },
            ProtocolSettings = settings.ToProtocolSettings(),
        };
        var item = new ConnectionListItem(profile, "RDP only", false);
        Connections.Add(item);
        SelectedConnection = item;
        IsViewOnly = EditViewOnly;
        ConnectionEditorError = string.Empty;
        IsEditingConnection = false;
        SessionStatusLabel = $"已儲存 {name}；目前保存在未加密的執行階段記憶體，尚未寫入磁碟";
    }

    [RelayCommand]
    private async Task OpenSelectedSessionAsync()
    {
        if (SelectedConnection is null)
        {
            return;
        }

        var connection = SelectedConnection.Profile with
        {
            DefaultAccessMode = IsViewOnly ? SessionAccessMode.ViewOnly : SessionAccessMode.Interactive,
        };
        await LaunchConnectionAsync(connection);
    }

    [RelayCommand]
    private async Task QuickConnectAsync()
    {
        var value = QuickConnectText.Trim();
        if (value.Length == 0)
        {
            SessionStatusLabel = "請輸入 RDP 主機名稱或 IP 位址";
            return;
        }

        var candidate = value.Contains("://", StringComparison.Ordinal)
            ? value
            : $"rdp://{value}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var endpoint) ||
            !string.Equals(endpoint.Scheme, "rdp", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(endpoint.Host))
        {
            SessionStatusLabel = "RDP 位址格式無效，請使用主機名稱、IP 或 host:port";
            return;
        }

        var connection = CreateConnection(value, "rdp", endpoint.ToString()) with
        {
            DefaultAccessMode = IsViewOnly ? SessionAccessMode.ViewOnly : SessionAccessMode.Interactive,
        };
        await LaunchConnectionAsync(connection);
    }

    private async Task LaunchConnectionAsync(ConnectionProfile connection)
    {
        var request = _sessionWorkspace.RequestOpen(connection);
        if (request.RequiresChoice)
        {
            SessionStatusLabel = "已有工作階段：請選擇切換既有分頁或另開工作階段";
            return;
        }

        var session = _sessionWorkspace.OpenNew(connection);
        if (string.Equals(connection.ProtocolId, "vnc", StringComparison.OrdinalIgnoreCase))
        {
            await LaunchVncAsync(connection, session.Id);
            return;
        }

        if (!string.Equals(connection.ProtocolId, "rdp", StringComparison.OrdinalIgnoreCase))
        {
            SessionStatusLabel = $"{session.State} · 等待 {connection.ProtocolId.ToUpperInvariant()} Adapter Host";
            return;
        }

        try
        {
            await _rdpLauncher.LaunchAsync(new RdpExternalLaunchRequest
            {
                Endpoint = connection.Endpoint,
                AccessMode = connection.DefaultAccessMode,
                Display = connection.Display,
                Settings = RdpConnectionSettings.FromProtocolSettings(connection.ProtocolSettings),
            });
            _sessionWorkspace.SetState(session.Id, SessionState.ExternalClientLaunched);
            SessionStatusLabel = "已啟動平台 RDP 用戶端 · 密碼由用戶端安全提示";
        }
        catch (Exception exception) when (
            exception is NotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _sessionWorkspace.SetState(session.Id, SessionState.Faulted, exception.Message);
            SessionStatusLabel = exception.Message;
        }
    }

    private async Task LaunchVncAsync(ConnectionProfile connection, SessionId sessionId)
    {
        await StopVncAsync();
        _vncCancellation = new CancellationTokenSource();
        _vncFrameSink = new AvaloniaRfbFrameSink(frame => RemoteFrame = frame);
        _vncClient = new RfbClient(new TcpRfbTransportFactory(), _vncFrameSink);
        try
        {
            var server = await _vncClient.ConnectAsync(new RfbConnectionOptions
            {
                Endpoint = connection.Endpoint,
                AccessMode = connection.DefaultAccessMode,
            }, _vncCancellation.Token);
            _sessionWorkspace.SetState(sessionId, SessionState.Connected);
            OnPropertyChanged(nameof(IsVncSessionActive));
            SessionStatusLabel = $"VNC 已連線 · {server.Name} · {server.Width} × {server.Height}";
            _ = ObserveVncAsync(_vncClient, sessionId, _vncCancellation.Token);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException)
        {
            _sessionWorkspace.SetState(sessionId, SessionState.Faulted, exception.Message);
            SessionStatusLabel = $"VNC 連線失敗：{exception.Message}";
            await StopVncAsync();
        }
    }

    private async Task ObserveVncAsync(RfbClient client, SessionId sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await client.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            _sessionWorkspace.SetState(sessionId, SessionState.Faulted, exception.Message);
            SessionStatusLabel = $"VNC 工作階段中斷：{exception.Message}";
        }
    }

    private async Task StopVncAsync()
    {
        _vncCancellation?.Cancel();
        _vncCancellation?.Dispose();
        _vncCancellation = null;
        if (_vncClient is not null)
        {
            await _vncClient.DisposeAsync();
            _vncClient = null;
        }

        _vncFrameSink?.Dispose();
        _vncFrameSink = null;
        RemoteFrame = null;
        OnPropertyChanged(nameof(IsVncSessionActive));
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
