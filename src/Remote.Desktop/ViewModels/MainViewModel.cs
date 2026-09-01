using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Text;
using Remote.Application;
using Remote.Application.Connections;
using Remote.Application.Sessions;
using Remote.Infrastructure.Processes;
using Remote.Infrastructure.Protocols.Rdp;
using Remote.Infrastructure.Protocols.Vnc;
using Remote.Infrastructure.Protocols.Ssh;
using Remote.Infrastructure.Security;
using Remote.Infrastructure.Storage;
using Remote.Infrastructure.Vaults;
using Remote.Application.Vaults;
using Remote.Application.Credentials;
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
    private SshTerminalSession? _sshSession;
    private CancellationTokenSource? _sshCancellation;
    private readonly EncryptedWorkspaceRepository _workspaceRepository;
    private readonly EncryptedVaultArchiveService _vaultArchiveService;
    private readonly string _workspacePath;
    private readonly List<ConnectionFolder> _folders = [];
    private CredentialVault? _vault;
    private string _activeMasterPassword = string.Empty;
    private VaultId _primaryVaultId = new(Guid.NewGuid());
    private VaultAutoLockController _autoLockController = new(new VaultLockSettings());

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
        _workspaceRepository = new EncryptedWorkspaceRepository(
            new EncryptedWorkspaceFile(new WorkspaceCryptography()));
        _vaultArchiveService = new EncryptedVaultArchiveService(new WorkspaceCryptography());
        _workspacePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Remote",
            "workspace.rmtw");
        var productionFolder = new ConnectionFolder { Id = FolderId.New(), Name = "Production" };
        var labFolder = new ConnectionFolder { Id = FolderId.New(), Name = "Lab" };
        _folders.AddRange([productionFolder, labFolder]);
        var windowsProd = CreateConnection("Windows Prod", "rdp", "rdp://10.20.0.24:3389") with { FolderId = productionFolder.Id };
        var designMac = CreateConnection("Design Mac", "vnc", "vnc://10.20.0.31:5900") with { FolderId = productionFolder.Id };
        var labSsh = CreateConnection("Lab SSH", "ssh2", "ssh://10.20.1.18:22") with { FolderId = labFolder.Id };
        var financeVm = CreateConnection("Finance VM", "rdp", "rdp://10.20.2.12:3389") with { FolderId = productionFolder.Id };
        var routerConsole = CreateConnection("Router Console", "https", "https://example.com") with { FolderId = labFolder.Id };
        Connections =
        [
            new(windowsProd, "RDP only", true),
            new(designMac, "VNC only", false),
            new(labSsh, "SSH2 only", false),
            new(financeVm, "RDP only", false),
            new(routerConsole, "HTTPS only", false),
        ];
        ConnectionTree = BuildConnectionTree([productionFolder, labFolder], Connections);
        selectedConnection = Connections[0];
        selectedTreeItem = FindConnectionTreeItem(ConnectionTree, windowsProd.Id);
        UpdateAccessMode();
    }

    public string Title => "Remote";

    public ObservableCollection<ConnectionListItem> Connections { get; }

    public ObservableCollection<CredentialDefinition> VaultCredentials { get; } = [];

    public IReadOnlyList<string> IdentityProtocols { get; } = ["rdp", "vnc", "ssh2", "http", "https", "terminal"];

    public ObservableCollection<ConnectionTreeDisplayItem> ConnectionTree { get; }

    [ObservableProperty]
    private ConnectionTreeDisplayItem? selectedTreeItem;

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
    private string editProtocol = "rdp";

    public IReadOnlyList<string> ConnectionProtocols { get; } = ["rdp", "vnc", "ssh2", "https", "http"];

    public bool IsEditingRdp => string.Equals(EditProtocol, "rdp", StringComparison.OrdinalIgnoreCase);

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

    [ObservableProperty]
    private string sessionUsername = string.Empty;

    [ObservableProperty]
    private string sessionPassword = string.Empty;

    [ObservableProperty]
    private string expectedHostKey = string.Empty;

    [ObservableProperty]
    private bool trustUnknownHostKey;

    [ObservableProperty]
    private string terminalText = string.Empty;

    [ObservableProperty]
    private string terminalInput = string.Empty;

    [ObservableProperty]
    private bool isTerminalActive;

    [ObservableProperty]
    private bool isWebSessionActive;

    [ObservableProperty]
    private Uri? webSource;

    [ObservableProperty]
    private string webAddress = string.Empty;

    [ObservableProperty]
    private bool isVaultPanelOpen;

    [ObservableProperty]
    private bool isVaultLocked = true;

    [ObservableProperty]
    private string vaultMasterPassword = string.Empty;

    [ObservableProperty]
    private string vaultMessage = "Vault 已鎖定";

    [ObservableProperty]
    private string newIdentityName = string.Empty;

    [ObservableProperty]
    private string newIdentityProtocol = "ssh2";

    [ObservableProperty]
    private string newIdentityUsername = string.Empty;

    [ObservableProperty]
    private string newIdentityDomain = string.Empty;

    [ObservableProperty]
    private string newIdentitySecret = string.Empty;

    [ObservableProperty]
    private CredentialDefinition? selectedVaultCredential;

    [ObservableProperty]
    private InactivityLockInterval selectedLockInterval = InactivityLockInterval.FiveMinutes;

    [ObservableProperty]
    private bool lockOnSystemSleep = true;

    [ObservableProperty]
    private bool lockOnSessionLogout = true;

    public IReadOnlyList<InactivityLockInterval> LockIntervalOptions { get; } =
    [
        InactivityLockInterval.OneMinute,
        InactivityLockInterval.FiveMinutes,
        InactivityLockInterval.FifteenMinutes,
        InactivityLockInterval.ThirtyMinutes,
        InactivityLockInterval.Disabled,
    ];

    public bool RequiresReducedSecurityWarning => CurrentLockSettings.RequiresReducedSecurityWarning;

    public string VaultStatusLabel => IsVaultLocked ? "🔒 Vault 已鎖定" : "🔓 Vault 已解鎖";

    public bool WorkspaceExists => File.Exists(_workspacePath);

    public bool IsSshSelected => string.Equals(SelectedConnection?.Profile.ProtocolId, "ssh2", StringComparison.OrdinalIgnoreCase);

    public bool IsVncSelected => string.Equals(SelectedConnection?.Profile.ProtocolId, "vnc", StringComparison.OrdinalIgnoreCase);

    public bool ShowSessionPlaceholder => RemoteFrame is null && !IsTerminalActive && !IsWebSessionActive;

    public bool IsVncSessionActive => _vncClient?.IsConnected is true;

    public Task<bool> SendVncKeyAsync(uint keySym, bool isDown) =>
        _vncClient?.SendKeyAsync(keySym, isDown) ?? Task.FromResult(false);

    public Task<bool> SendVncPointerAsync(byte buttonMask, ushort x, ushort y) =>
        _vncClient?.SendPointerAsync(buttonMask, x, y) ?? Task.FromResult(false);

    public async Task ShutdownAsync()
    {
        await StopVncAsync();
        await StopSshAsync();
        LockVault();
    }

    partial void OnIsVaultLockedChanged(bool value) => OnPropertyChanged(nameof(VaultStatusLabel));

    partial void OnEditProtocolChanged(string value)
    {
        EditPort = value switch
        {
            "rdp" => "3389",
            "vnc" => "5900",
            "ssh2" => "22",
            "https" => "443",
            "http" => "80",
            _ => EditPort,
        };
        OnPropertyChanged(nameof(IsEditingRdp));
    }

    partial void OnSelectedLockIntervalChanged(InactivityLockInterval value) => RefreshAutoLockPolicy();

    partial void OnLockOnSystemSleepChanged(bool value) => RefreshAutoLockPolicy();

    partial void OnLockOnSessionLogoutChanged(bool value) => RefreshAutoLockPolicy();

    public void RecordUserActivity() => _autoLockController.RecordActivity();

    public void EvaluateVaultAutoLock()
    {
        if (!IsVaultLocked && _autoLockController.EvaluateInactivity() is VaultLockReason.Inactivity)
        {
            LockVault();
            VaultMessage = "Vault 已因閒置逾時自動鎖定";
        }
    }

    [RelayCommand]
    private void OpenVaultPanel() => IsVaultPanelOpen = true;

    [RelayCommand]
    private void CloseVaultPanel()
    {
        VaultMasterPassword = string.Empty;
        NewIdentitySecret = string.Empty;
        IsVaultPanelOpen = false;
    }

    [RelayCommand]
    private async Task UnlockVaultAsync()
    {
        if (VaultMasterPassword.Length < 8)
        {
            VaultMessage = "主密碼至少需要 8 個字元";
            return;
        }

        try
        {
            CredentialVault vault;
            if (File.Exists(_workspacePath))
            {
                var document = await _workspaceRepository.LoadAsync(_workspacePath, VaultMasterPassword);
                if (document.EncryptedPrimaryVault is { Length: > 0 } archive)
                {
                    try
                    {
                        vault = _vaultArchiveService.Import(archive, VaultMasterPassword);
                    }
                    finally
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(archive);
                    }
                }
                else
                {
                    vault = new CredentialVault();
                }
                LoadWorkspaceDocument(document);
            }
            else
            {
                vault = new CredentialVault();
            }

            _vault?.Dispose();
            _vault = vault;
            _activeMasterPassword = VaultMasterPassword;
            VaultMasterPassword = string.Empty;
            IsVaultLocked = false;
            _autoLockController.RecordActivity();
            RefreshVaultCredentials();
            VaultMessage = File.Exists(_workspacePath) ? "Vault 已安全解鎖" : "已建立新的本機主 Vault";
            if (!File.Exists(_workspacePath))
            {
                await SaveWorkspaceAsync();
            }
        }
        catch (Exception exception) when (exception is WorkspaceUnlockException or InvalidDataException or NotSupportedException)
        {
            VaultMasterPassword = string.Empty;
            VaultMessage = exception.Message;
        }
    }

    [RelayCommand]
    private void LockVault()
    {
        _vault?.Dispose();
        _vault = null;
        _activeMasterPassword = string.Empty;
        VaultMasterPassword = string.Empty;
        SessionPassword = string.Empty;
        NewIdentitySecret = string.Empty;
        VaultCredentials.Clear();
        IsVaultLocked = true;
        VaultMessage = "Vault 已鎖定；敏感內容已從執行階段清除";
    }

    [RelayCommand]
    private async Task AddIdentityCardAsync()
    {
        if (_vault is null || IsVaultLocked)
        {
            VaultMessage = "請先解鎖 Vault";
            return;
        }

        var name = NewIdentityName.Trim();
        var protocol = NewIdentityProtocol.Trim().ToLowerInvariant();
        var username = NewIdentityUsername.Trim();
        if (name.Length == 0 || username.Length == 0 || NewIdentitySecret.Length == 0 ||
            protocol is not ("rdp" or "vnc" or "ssh2" or "http" or "https" or "terminal"))
        {
            VaultMessage = "名稱、支援的協定、使用者名稱與密碼皆為必填";
            return;
        }

        var definition = new CredentialDefinition
        {
            Id = new CredentialId(Guid.NewGuid()),
            VaultId = _primaryVaultId,
            Name = name,
            Kind = CredentialKind.UsernamePassword,
            ProtocolScope = protocol,
            Username = username,
            Domain = string.IsNullOrWhiteSpace(NewIdentityDomain) ? null : NewIdentityDomain.Trim(),
        };
        var secret = Encoding.UTF8.GetBytes(NewIdentitySecret);
        try
        {
            _vault.Add(definition, secret);
            NewIdentitySecret = string.Empty;
            NewIdentityName = string.Empty;
            NewIdentityUsername = string.Empty;
            NewIdentityDomain = string.Empty;
            RefreshVaultCredentials();
            await SaveWorkspaceAsync();
            VaultMessage = $"身份卡「{definition.Name}」已加密儲存";
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        }
    }

    [RelayCommand]
    private async Task AssignIdentityCardAsync()
    {
        if (SelectedConnection is null || SelectedVaultCredential is null || _vault is null || IsVaultLocked)
        {
            VaultMessage = "請選取身份卡與目標連線";
            return;
        }

        if (!string.Equals(
                SelectedVaultCredential.ProtocolScope,
                SelectedConnection.Profile.ProtocolId,
                StringComparison.OrdinalIgnoreCase))
        {
            VaultMessage = $"此身份卡僅供 {SelectedVaultCredential.ProtocolScope.ToUpperInvariant()} 使用";
            return;
        }

        var index = Connections.IndexOf(SelectedConnection);
        var updated = SelectedConnection with
        {
            Profile = SelectedConnection.Profile with
            {
                Credential = ConnectionCredentialReference.IdentityCard(
                    SelectedVaultCredential.VaultId,
                    SelectedVaultCredential.Id),
            },
            IdentityScope = $"{SelectedVaultCredential.ProtocolScope.ToUpperInvariant()} only",
        };
        Connections[index] = updated;
        RebuildConnectionTree(updated.Profile.Id);
        await SaveWorkspaceAsync();
        VaultMessage = $"已將「{SelectedVaultCredential.Name}」指派給 {updated.Name}";
    }

    partial void OnSelectedConnectionChanged(ConnectionListItem? value)
    {
        IsViewOnly = value?.Profile.DefaultAccessMode is SessionAccessMode.ViewOnly;
        OnPropertyChanged(nameof(IsSshSelected));
        OnPropertyChanged(nameof(IsVncSelected));
    }

    partial void OnSelectedTreeItemChanged(ConnectionTreeDisplayItem? value)
    {
        if (value?.Connection is { } connection)
        {
            SelectedConnection = Connections.FirstOrDefault(item => item.Profile.Id == connection.Id);
        }
    }

    partial void OnRemoteFrameChanged(WriteableBitmap? value) =>
        OnPropertyChanged(nameof(ShowSessionPlaceholder));

    partial void OnIsTerminalActiveChanged(bool value) =>
        OnPropertyChanged(nameof(ShowSessionPlaceholder));

    partial void OnIsWebSessionActiveChanged(bool value) =>
        OnPropertyChanged(nameof(ShowSessionPlaceholder));

    public string RuntimeStatus => _sessionService.GetStatus().State;

    public string ProtocolName => _protocol.Descriptor.DisplayName;

    partial void OnIsViewOnlyChanged(bool value) => UpdateAccessMode();

    [RelayCommand]
    private void BeginNewConnection()
    {
        EditName = string.Empty;
        EditProtocol = "rdp";
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
    private async Task SaveConnectionAsync()
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
            if (IsEditingRdp)
            {
                settings.Validate();
            }
        }
        catch (ArgumentException exception)
        {
            ConnectionEditorError = exception.Message;
            return;
        }

        var scheme = EditProtocol == "ssh2" ? "ssh" : EditProtocol;
        var endpoint = new UriBuilder(scheme, host, port).Uri;
        var profile = new ConnectionProfile
        {
            Id = ConnectionId.New(),
            Name = name,
            Endpoint = endpoint,
            ProtocolId = EditProtocol,
            DefaultAccessMode = EditViewOnly ? SessionAccessMode.ViewOnly : SessionAccessMode.Interactive,
            Display = new DisplayPreferences
            {
                MonitorSelection = EditUseAllMonitors ? MonitorSelection.All : MonitorSelection.Single,
            },
            ProtocolSettings = IsEditingRdp ? settings.ToProtocolSettings() : new ProtocolSettings(),
        };
        var item = new ConnectionListItem(profile, $"{EditProtocol.ToUpperInvariant()} only", false);
        Connections.Add(item);
        ConnectionTree.Add(ConnectionTreeDisplayItem.ForConnection(item.Profile));
        SelectedConnection = item;
        SelectedTreeItem = ConnectionTree[^1];
        IsViewOnly = EditViewOnly;
        ConnectionEditorError = string.Empty;
        IsEditingConnection = false;
        if (!IsVaultLocked)
        {
            await SaveWorkspaceAsync();
            SessionStatusLabel = $"已加密儲存 {name}";
        }
        else
        {
            SessionStatusLabel = $"已加入 {name}；解鎖 Vault 後才能加密寫入磁碟";
        }
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

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var webEndpoint = WebNavigationPolicy.ParseHttpEndpoint(value);
                await LaunchConnectionAsync(CreateConnection(value, webEndpoint.Scheme, webEndpoint.ToString()) with
                {
                    DefaultAccessMode = IsViewOnly ? SessionAccessMode.ViewOnly : SessionAccessMode.Interactive,
                });
            }
            catch (ArgumentException exception)
            {
                SessionStatusLabel = exception.Message;
            }

            return;
        }

        var candidate = value.Contains("://", StringComparison.Ordinal) ? value : $"rdp://{value}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var endpoint) ||
            !string.Equals(endpoint.Scheme, "rdp", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(endpoint.Host))
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
            await StopSshAsync();
            CloseWebSession();
            await LaunchVncAsync(connection, session.Id);
            return;
        }

        if (string.Equals(connection.ProtocolId, "ssh2", StringComparison.OrdinalIgnoreCase))
        {
            await StopVncAsync();
            CloseWebSession();
            await LaunchSshAsync(connection, session.Id);
            return;
        }

        if (connection.ProtocolId is "http" or "https")
        {
            await StopVncAsync();
            await StopSshAsync();
            WebSource = WebNavigationPolicy.ParseHttpEndpoint(connection.Endpoint.ToString());
            WebAddress = WebSource.ToString();
            IsWebSessionActive = true;
            _sessionWorkspace.SetState(session.Id, SessionState.Connected);
            SessionStatusLabel = $"{connection.ProtocolId.ToUpperInvariant()} 已載入 · {connection.Endpoint.Host}";
            return;
        }

        if (!string.Equals(connection.ProtocolId, "rdp", StringComparison.OrdinalIgnoreCase))
        {
            SessionStatusLabel = $"{session.State} · 等待 {connection.ProtocolId.ToUpperInvariant()} Adapter Host";
            return;
        }

        await StopVncAsync();
        await StopSshAsync();
        CloseWebSession();

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

    [RelayCommand]
    private void NavigateWeb()
    {
        if (IsViewOnly)
        {
            SessionStatusLabel = "VIEW ONLY：網址導覽已阻擋";
            return;
        }

        try
        {
            WebSource = WebNavigationPolicy.ParseHttpEndpoint(WebAddress);
            WebAddress = WebSource.ToString();
            SessionStatusLabel = $"正在載入 {WebSource.Host}";
        }
        catch (ArgumentException exception)
        {
            SessionStatusLabel = exception.Message;
        }
    }

    private void CloseWebSession()
    {
        IsWebSessionActive = false;
        WebSource = null;
        WebAddress = string.Empty;
    }

    private async Task LaunchSshAsync(ConnectionProfile connection, SessionId sessionId)
    {
        byte[]? vaultSecret = null;
        var username = SessionUsername.Trim();
        var password = SessionPassword;
        if (connection.Credential.Kind is CredentialReferenceKind.IdentityCard &&
            connection.Credential.CredentialId is { } credentialId &&
            _vault is not null && !IsVaultLocked)
        {
            var definition = _vault.Credentials.FirstOrDefault(item => item.Id == credentialId);
            if (definition is not null)
            {
                username = definition.Username ?? string.Empty;
                vaultSecret = _vault.Reveal(credentialId);
                password = Encoding.UTF8.GetString(vaultSecret);
            }
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            SessionStatusLabel = "SSH2 需要已解鎖的身份卡，或右側暫時登入資料";
            if (vaultSecret is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(vaultSecret);
            }
            return;
        }

        await StopSshAsync();
        _sshCancellation = new CancellationTokenSource();
        _sshSession = new SshTerminalSession();
        SshHostKeyInfo? acceptedHostKey = null;
        try
        {
            await _sshSession.ConnectAsync(new SshSessionOptions
            {
                Endpoint = connection.Endpoint,
                Username = username,
                Password = password,
                ExpectedHostKeySha256 = string.IsNullOrWhiteSpace(ExpectedHostKey) ? null : ExpectedHostKey.Trim(),
                ConfirmUnknownHostKey = TrustUnknownHostKey
                    ? key => { acceptedHostKey = key; return true; }
                    : null,
                AccessMode = connection.DefaultAccessMode,
            }, _sshCancellation.Token);
            SessionPassword = string.Empty;
            if (acceptedHostKey is not null)
            {
                ExpectedHostKey = $"SHA256:{acceptedHostKey.Sha256Fingerprint}";
                TrustUnknownHostKey = false;
            }

            TerminalText = string.Empty;
            IsTerminalActive = true;
            _sessionWorkspace.SetState(sessionId, SessionState.Connected);
            SessionStatusLabel = $"SSH2 已連線 · {connection.Endpoint.Host} · {AccessModeLabel}";
            _ = ObserveSshAsync(_sshSession, sessionId, _sshCancellation.Token);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Renci.SshNet.Common.SshException)
        {
            _sessionWorkspace.SetState(sessionId, SessionState.Faulted, exception.Message);
            SessionStatusLabel = $"SSH2 連線失敗：{exception.Message}";
            SessionPassword = string.Empty;
            await StopSshAsync();
        }
        finally
        {
            if (vaultSecret is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(vaultSecret);
            }
        }
    }

    [RelayCommand]
    private async Task SendTerminalInputAsync()
    {
        var input = TerminalInput;
        if (_sshSession is null || input.Length == 0)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(input + "\n");
        try
        {
            if (await _sshSession.WriteAsync(bytes))
            {
                TerminalInput = string.Empty;
            }
            else
            {
                SessionStatusLabel = "VIEW ONLY：SSH2 輸入已在協定層阻擋";
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task ObserveSshAsync(
        SshTerminalSession session,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await session.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, read);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    TerminalText += text;
                    if (TerminalText.Length > 1_000_000)
                    {
                        TerminalText = TerminalText[^750_000..];
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or Renci.SshNet.Common.SshException)
        {
            _sessionWorkspace.SetState(sessionId, SessionState.Faulted, exception.Message);
            await Dispatcher.UIThread.InvokeAsync(() => SessionStatusLabel = $"SSH2 工作階段中斷：{exception.Message}");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private async Task StopSshAsync()
    {
        _sshCancellation?.Cancel();
        _sshCancellation?.Dispose();
        _sshCancellation = null;
        if (_sshSession is not null)
        {
            await _sshSession.DisposeAsync();
            _sshSession = null;
        }

        IsTerminalActive = false;
    }

    private void RefreshVaultCredentials()
    {
        VaultCredentials.Clear();
        if (_vault is null || _vault.IsLocked)
        {
            return;
        }

        foreach (var credential in _vault.Credentials.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            VaultCredentials.Add(credential);
        }

        if (VaultCredentials.FirstOrDefault() is { } first)
        {
            _primaryVaultId = first.VaultId;
        }
    }

    private async Task SaveWorkspaceAsync()
    {
        if (_vault is null || IsVaultLocked || _activeMasterPassword.Length == 0)
        {
            throw new InvalidOperationException("The Vault must be unlocked before saving the Workspace.");
        }

        var encryptedVault = _vaultArchiveService.Export(_vault, _activeMasterPassword);
        try
        {
            var identityCards = _vault.Credentials
                .Where(credential => credential.Kind is CredentialKind.UsernamePassword)
                .Select(credential => new IdentityCard
                {
                    VaultId = credential.VaultId,
                    CredentialId = credential.Id,
                    Name = credential.Name,
                    ProtocolId = credential.ProtocolScope,
                    Username = credential.Username ?? string.Empty,
                    Domain = credential.Domain,
                })
                .ToArray();
            await _workspaceRepository.SaveAsync(_workspacePath, new WorkspaceDocument
            {
                Folders = _folders.ToArray(),
                Connections = Connections.Select(item => item.Profile).ToArray(),
                IdentityCards = identityCards,
                EncryptedPrimaryVault = encryptedVault,
            }, _activeMasterPassword);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(encryptedVault);
        }
    }

    private void LoadWorkspaceDocument(WorkspaceDocument document)
    {
        _folders.Clear();
        _folders.AddRange(document.Folders);
        Connections.Clear();
        foreach (var connection in document.Connections)
        {
            Connections.Add(new ConnectionListItem(connection, $"{connection.ProtocolId.ToUpperInvariant()} only", false));
        }

        ConnectionTree.Clear();
        foreach (var item in BuildConnectionTree(_folders, Connections))
        {
            ConnectionTree.Add(item);
        }

        SelectedConnection = Connections.FirstOrDefault();
        SelectedTreeItem = SelectedConnection is null
            ? null
            : FindConnectionTreeItem(ConnectionTree, SelectedConnection.Profile.Id);
    }

    private void RebuildConnectionTree(ConnectionId selectedId)
    {
        ConnectionTree.Clear();
        foreach (var item in BuildConnectionTree(_folders, Connections))
        {
            ConnectionTree.Add(item);
        }

        SelectedConnection = Connections.FirstOrDefault(item => item.Profile.Id == selectedId);
        SelectedTreeItem = FindConnectionTreeItem(ConnectionTree, selectedId);
    }

    private VaultLockSettings CurrentLockSettings => new()
    {
        InactivityInterval = SelectedLockInterval,
        LockOnSystemSleep = LockOnSystemSleep,
        LockOnSessionLogout = LockOnSessionLogout,
    };

    private void RefreshAutoLockPolicy()
    {
        _autoLockController = new VaultAutoLockController(CurrentLockSettings);
        OnPropertyChanged(nameof(RequiresReducedSecurityWarning));
        if (RequiresReducedSecurityWarning)
        {
            VaultMessage = "警告：停用閒置、睡眠或登出鎖定會降低敏感資料安全性";
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

    private static ObservableCollection<ConnectionTreeDisplayItem> BuildConnectionTree(
        IReadOnlyList<ConnectionFolder> folders,
        IEnumerable<ConnectionListItem> connections)
    {
        var profiles = connections.Select(item => item.Profile).ToArray();
        var nodes = new ConnectionTreeBuilder().Build(folders, profiles);
        var result = new ObservableCollection<ConnectionTreeDisplayItem>(nodes.Select(ConvertNode));
        foreach (var connection in profiles.Where(profile => profile.FolderId is null))
        {
            result.Add(ConnectionTreeDisplayItem.ForConnection(connection));
        }

        return result;
    }

    private static ConnectionTreeDisplayItem ConvertNode(ConnectionTreeNode node)
    {
        var children = node.Children.Select(ConvertNode)
            .Concat(node.Connections.Select(ConnectionTreeDisplayItem.ForConnection));
        return ConnectionTreeDisplayItem.ForFolder(node.Folder, children);
    }

    private static ConnectionTreeDisplayItem? FindConnectionTreeItem(
        IEnumerable<ConnectionTreeDisplayItem> items,
        ConnectionId id)
    {
        foreach (var item in items)
        {
            if (item.Connection?.Id == id)
            {
                return item;
            }

            var nested = FindConnectionTreeItem(item.Children, id);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}

public sealed record ConnectionTreeDisplayItem(
    string Name,
    string Detail,
    bool IsFolder,
    ConnectionProfile? Connection,
    ObservableCollection<ConnectionTreeDisplayItem> Children)
{
    public string Glyph => IsFolder ? "▾" : "●";

    public static ConnectionTreeDisplayItem ForFolder(
        ConnectionFolder folder,
        IEnumerable<ConnectionTreeDisplayItem> children) =>
        new(folder.Name, "資料夾", true, null, new(children));

    public static ConnectionTreeDisplayItem ForConnection(ConnectionProfile connection) =>
        new(
            connection.Name,
            $"{connection.ProtocolId.ToUpperInvariant()} · {connection.Endpoint.Host}:{connection.Endpoint.Port}",
            false,
            connection,
            []);
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
