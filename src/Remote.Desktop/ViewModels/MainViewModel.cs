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
using Remote.Infrastructure.Protocols.Terminal;
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
    private LocalTerminalSession? _localTerminal;
    private CancellationTokenSource? _localTerminalCancellation;
    private readonly TerminalOutputDecoder _terminalOutputDecoder = new();
    private readonly EncryptedWorkspaceRepository _workspaceRepository;
    private readonly EncryptedVaultArchiveService _vaultArchiveService;
    private readonly EncryptedWorkspaceBackupService _backupService = new();
    private readonly string _workspacePath;
    private readonly string _backupDirectory;
    private readonly List<ConnectionFolder> _folders = [];
    private ConnectionId? _editingConnectionId;
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
                new MacOsRdpLaunchSpecFactory(),
                credentialStore: new WindowsRdpCredentialStore()))
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
        _backupDirectory = Path.Combine(Path.GetDirectoryName(_workspacePath)!, "Backups");
        var productionFolder = new ConnectionFolder { Id = FolderId.New(), Name = "Production" };
        var labFolder = new ConnectionFolder { Id = FolderId.New(), Name = "Lab" };
        _folders.AddRange([productionFolder, labFolder]);
        var windowsProd = CreateConnection("Windows Prod", "rdp", "rdp://10.20.0.24:3389") with { FolderId = productionFolder.Id };
        var designMac = CreateConnection("Design Mac", "vnc", "vnc://10.20.0.31:5900") with { FolderId = productionFolder.Id };
        var labSsh = CreateConnection("Lab SSH", "ssh2", "ssh://10.20.1.18:22") with { FolderId = labFolder.Id };
        var financeVm = CreateConnection("Finance VM", "rdp", "rdp://10.20.2.12:3389") with { FolderId = productionFolder.Id };
        var routerConsole = CreateConnection("Router Console", "https", "https://example.com") with { FolderId = labFolder.Id };
        var localShell = CreateConnection("Local Shell", "terminal", "terminal://localhost") with { FolderId = labFolder.Id };
        Connections =
        [
            new(windowsProd, "RDP only", true),
            new(designMac, "VNC only", false),
            new(labSsh, "SSH2 only", false),
            new(financeVm, "RDP only", false),
            new(routerConsole, "HTTPS only", false),
            new(localShell, "TERMINAL only", false),
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

    public IReadOnlyList<string> ConnectionProtocols { get; } = ["rdp", "vnc", "ssh2", "https", "http", "terminal"];

    public bool IsEditingRdp => string.Equals(EditProtocol, "rdp", StringComparison.OrdinalIgnoreCase);

    public bool IsEditingNetwork => !string.Equals(EditProtocol, "terminal", StringComparison.OrdinalIgnoreCase);

    public string ConnectionEditorTitle => _editingConnectionId is null ? "新增連線" : "編輯連線";

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
    private bool automaticBackupEnabled;

    [ObservableProperty]
    private bool isNotificationOpen;

    [ObservableProperty]
    private string notificationMessage = string.Empty;

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
        await StopLocalTerminalAsync();
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
            "terminal" => "0",
            _ => EditPort,
        };
        OnPropertyChanged(nameof(IsEditingRdp));
        OnPropertyChanged(nameof(IsEditingNetwork));
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

    [RelayCommand]
    private async Task DeleteIdentityCardAsync()
    {
        if (SelectedVaultCredential is not { } credential || _vault is null || IsVaultLocked)
        {
            VaultMessage = "請先選取要永久刪除的身份卡";
            return;
        }

        var cleanup = new CredentialReferenceCleaner().Clear(
            credential.VaultId,
            credential.Id,
            Connections.Select(item => item.Profile),
            _folders);
        if (!_vault.Delete(credential.Id))
        {
            VaultMessage = "身份卡已不存在";
            return;
        }

        _folders.Clear();
        _folders.AddRange(cleanup.Folders);
        foreach (var profile in cleanup.Connections)
        {
            var existing = Connections.First(item => item.Profile.Id == profile.Id);
            var index = Connections.IndexOf(existing);
            Connections[index] = existing with
            {
                Profile = profile,
                IdentityScope = profile.Credential.Kind is CredentialReferenceKind.None
                    ? "No identity"
                    : existing.IdentityScope,
            };
        }

        if (SelectedConnection is { } selectedConnection)
        {
            RebuildConnectionTree(selectedConnection.Profile.Id);
        }
        SelectedVaultCredential = null;
        RefreshVaultCredentials();
        await SaveWorkspaceAsync();
        NotificationMessage =
            $"身份卡「{credential.Name}」及其歷史版本已永久刪除。已清除 " +
            $"{cleanup.ClearedConnectionReferences} 個連線與 {cleanup.ClearedFolderReferences} 個群組引用。" +
            "既有的加密備份仍可能保留舊資料。";
        IsNotificationOpen = true;
        VaultMessage = "永久刪除完成";
    }

    [RelayCommand]
    private void DismissNotification() => IsNotificationOpen = false;

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        try
        {
            var backupPath = await _backupService.CreateAsync(
                _workspacePath,
                _backupDirectory,
                DateTimeOffset.Now);
            NotificationMessage = $"已建立本機加密備份：{backupPath}";
            IsNotificationOpen = true;
            VaultMessage = "手動備份完成";
        }
        catch (IOException exception)
        {
            VaultMessage = $"備份失敗：{exception.Message}";
        }
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
        _editingConnectionId = null;
        OnPropertyChanged(nameof(ConnectionEditorTitle));
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
    private void BeginEditConnection()
    {
        if (SelectedConnection is not { } selected)
        {
            SessionStatusLabel = "請先選取要編輯的連線";
            return;
        }

        var profile = selected.Profile;
        var settings = RdpConnectionSettings.FromProtocolSettings(profile.ProtocolSettings);
        _editingConnectionId = profile.Id;
        EditName = profile.Name;
        EditProtocol = profile.ProtocolId;
        EditHost = profile.Endpoint.Host;
        EditPort = profile.ProtocolId is "terminal"
            ? "0"
            : profile.Endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        EditGateway = settings.GatewayHost ?? string.Empty;
        EditUseAllMonitors = profile.Display.MonitorSelection is MonitorSelection.All;
        EditViewOnly = profile.DefaultAccessMode is SessionAccessMode.ViewOnly;
        EditRedirectClipboard = settings.RedirectClipboard;
        EditRedirectPrinters = settings.RedirectPrinters;
        EditRedirectDrives = settings.RedirectDrives;
        ConnectionEditorError = string.Empty;
        OnPropertyChanged(nameof(ConnectionEditorTitle));
        IsEditingConnection = true;
    }

    [RelayCommand]
    private void CancelConnectionEdit()
    {
        _editingConnectionId = null;
        OnPropertyChanged(nameof(ConnectionEditorTitle));
        ConnectionEditorError = string.Empty;
        IsEditingConnection = false;
    }

    [RelayCommand]
    private async Task SaveConnectionAsync()
    {
        var name = EditName.Trim();
        var host = EditHost.Trim();
        if (name.Length == 0 || (EditProtocol != "terminal" && host.Length == 0))
        {
            ConnectionEditorError = "名稱與主機為必填欄位";
            return;
        }

        if (EditProtocol != "terminal" &&
            (!int.TryParse(EditPort, out var parsedPort) || parsedPort is < 1 or > 65535))
        {
            ConnectionEditorError = "連接埠必須介於 1 到 65535";
            return;
        }

        var port = EditProtocol == "terminal" ? -1 : int.Parse(EditPort, System.Globalization.CultureInfo.InvariantCulture);
        if (EditProtocol != "terminal" && Uri.CheckHostName(host) is UriHostNameType.Unknown)
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
        var endpoint = EditProtocol == "terminal"
            ? new Uri("terminal://localhost")
            : new UriBuilder(scheme, host, port).Uri;
        var existing = _editingConnectionId is { } editingId
            ? Connections.FirstOrDefault(item => item.Profile.Id == editingId)
            : null;
        var profile = new ConnectionProfile
        {
            Id = existing?.Profile.Id ?? ConnectionId.New(),
            Name = name,
            Endpoint = endpoint,
            ProtocolId = EditProtocol,
            FolderId = existing?.Profile.FolderId,
            Credential = existing?.Profile.Credential ?? ConnectionCredentialReference.Inherited,
            DefaultAccessMode = EditViewOnly ? SessionAccessMode.ViewOnly : SessionAccessMode.Interactive,
            Display = new DisplayPreferences
            {
                MonitorSelection = EditUseAllMonitors ? MonitorSelection.All : MonitorSelection.Single,
            },
            ProtocolSettings = IsEditingRdp ? settings.ToProtocolSettings() : new ProtocolSettings(),
        };
        var item = new ConnectionListItem(
            profile,
            existing?.IdentityScope ?? $"{EditProtocol.ToUpperInvariant()} only",
            existing?.IsFavorite ?? false);
        if (existing is null)
        {
            Connections.Add(item);
        }
        else
        {
            Connections[Connections.IndexOf(existing)] = item;
        }

        RebuildConnectionTree(profile.Id);
        SelectedConnection = item;
        SelectedTreeItem = FindConnectionTreeItem(ConnectionTree, profile.Id);
        IsViewOnly = EditViewOnly;
        ConnectionEditorError = string.Empty;
        _editingConnectionId = null;
        OnPropertyChanged(nameof(ConnectionEditorTitle));
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
            await StopLocalTerminalAsync();
            CloseWebSession();
            await LaunchVncAsync(connection, session.Id);
            return;
        }

        if (string.Equals(connection.ProtocolId, "ssh2", StringComparison.OrdinalIgnoreCase))
        {
            await StopVncAsync();
            await StopLocalTerminalAsync();
            CloseWebSession();
            await LaunchSshAsync(connection, session.Id);
            return;
        }

        if (connection.ProtocolId is "http" or "https")
        {
            await StopVncAsync();
            await StopSshAsync();
            await StopLocalTerminalAsync();
            WebSource = WebNavigationPolicy.ParseHttpEndpoint(connection.Endpoint.ToString());
            WebAddress = WebSource.ToString();
            IsWebSessionActive = true;
            _sessionWorkspace.SetState(session.Id, SessionState.Connected);
            SessionStatusLabel = $"{connection.ProtocolId.ToUpperInvariant()} 已載入 · {connection.Endpoint.Host}";
            return;
        }

        if (string.Equals(connection.ProtocolId, "terminal", StringComparison.OrdinalIgnoreCase))
        {
            await StopVncAsync();
            await StopSshAsync();
            CloseWebSession();
            await LaunchLocalTerminalAsync(connection, session.Id);
            return;
        }

        if (!string.Equals(connection.ProtocolId, "rdp", StringComparison.OrdinalIgnoreCase))
        {
            SessionStatusLabel = $"{session.State} · 等待 {connection.ProtocolId.ToUpperInvariant()} Adapter Host";
            return;
        }

        await StopVncAsync();
        await StopSshAsync();
        await StopLocalTerminalAsync();
        CloseWebSession();

        byte[]? rdpSecret = null;
        try
        {
            string? username = null;
            if (ResolveCredentialDefinition(connection) is { } definition)
            {
                username = string.IsNullOrWhiteSpace(definition.Domain)
                    ? definition.Username
                    : $"{definition.Domain}\\{definition.Username}";
                rdpSecret = _vault!.Reveal(definition.Id);
            }

            await _rdpLauncher.LaunchAsync(new RdpExternalLaunchRequest
            {
                Endpoint = connection.Endpoint,
                Username = username,
                PasswordUtf8 = rdpSecret,
                AccessMode = connection.DefaultAccessMode,
                Display = connection.Display,
                Settings = RdpConnectionSettings.FromProtocolSettings(connection.ProtocolSettings),
            });
            _sessionWorkspace.SetState(session.Id, SessionState.ExternalClientLaunched);
            SessionStatusLabel = rdpSecret is null
                ? "已啟動平台 RDP 用戶端 · 尚未指派 ID Card"
                : "已使用 ID Card 啟動 RDP 自動登入";
        }
        catch (Exception exception) when (
            exception is NotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _sessionWorkspace.SetState(session.Id, SessionState.Faulted, exception.Message);
            SessionStatusLabel = exception.Message;
        }
        finally
        {
            if (rdpSecret is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(rdpSecret);
            }
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
        try
        {
            if (ResolveCredentialDefinition(connection) is { } definition)
            {
                username = definition.Username ?? string.Empty;
                vaultSecret = _vault!.Reveal(definition.Id);
                password = Encoding.UTF8.GetString(vaultSecret);
            }
        }
        catch (InvalidOperationException exception)
        {
            SessionStatusLabel = $"SSH2 身份卡無法使用：{exception.Message}";
            return;
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
            _terminalOutputDecoder.Reset();
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
        if ((_sshSession is null && _localTerminal is null) || input.Length == 0)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(input + "\r");
        try
        {
            var written = _sshSession is not null
                ? await _sshSession.WriteAsync(bytes)
                : await _localTerminal!.WriteAsync(bytes);
            if (written)
            {
                TerminalInput = string.Empty;
            }
            else
            {
                SessionStatusLabel = "VIEW ONLY：終端輸入已在協定層阻擋";
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

                var text = _terminalOutputDecoder.Decode(buffer.AsSpan(0, read));
                await Dispatcher.UIThread.InvokeAsync(() => AppendTerminalText(text));
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

    private async Task LaunchLocalTerminalAsync(ConnectionProfile connection, SessionId sessionId)
    {
        await StopLocalTerminalAsync();
        _localTerminalCancellation = new CancellationTokenSource();
        _localTerminal = new LocalTerminalSession();
        try
        {
            await _localTerminal.StartAsync(new LocalTerminalOptions
            {
                ShellPath = connection.ProtocolSettings.Get("shellPath"),
                WorkingDirectory = connection.ProtocolSettings.Get("workingDirectory")
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                AccessMode = connection.DefaultAccessMode,
            }, _localTerminalCancellation.Token);
            TerminalText = string.Empty;
            _terminalOutputDecoder.Reset();
            IsTerminalActive = true;
            _sessionWorkspace.SetState(sessionId, SessionState.Connected);
            SessionStatusLabel = $"本機 Terminal 已啟動 · PID {_localTerminal.ProcessId} · {AccessModeLabel}";
            _ = ObserveLocalTerminalAsync(_localTerminal, sessionId, _localTerminalCancellation.Token);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            _sessionWorkspace.SetState(sessionId, SessionState.Faulted, exception.Message);
            SessionStatusLabel = $"Terminal 啟動失敗：{exception.Message}";
            await StopLocalTerminalAsync();
        }
    }

    private async Task ObserveLocalTerminalAsync(
        LocalTerminalSession terminal,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await terminal.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => SessionStatusLabel = "本機 Terminal 已結束");
                    break;
                }

                var text = _terminalOutputDecoder.Decode(buffer.AsSpan(0, read));
                await Dispatcher.UIThread.InvokeAsync(() => AppendTerminalText(text));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _sessionWorkspace.SetState(sessionId, SessionState.Faulted, exception.Message);
            await Dispatcher.UIThread.InvokeAsync(() => SessionStatusLabel = $"Terminal 中斷：{exception.Message}");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private async Task StopLocalTerminalAsync()
    {
        _localTerminalCancellation?.Cancel();
        _localTerminalCancellation?.Dispose();
        _localTerminalCancellation = null;
        if (_localTerminal is not null)
        {
            await _localTerminal.DisposeAsync();
            _localTerminal = null;
        }

        IsTerminalActive = false;
    }

    private void AppendTerminalText(string text)
    {
        TerminalText += text;
        if (TerminalText.Length > 1_000_000)
        {
            TerminalText = TerminalText[^750_000..];
        }
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
            if (AutomaticBackupEnabled)
            {
                await _backupService.CreateAsync(
                    _workspacePath,
                    _backupDirectory,
                    DateTimeOffset.Now);
            }
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
        byte[]? vaultSecret = null;
        await StopVncAsync();
        _vncCancellation = new CancellationTokenSource();
        _vncFrameSink = new AvaloniaRfbFrameSink(frame => RemoteFrame = frame);
        _vncClient = new RfbClient(new TcpRfbTransportFactory(), _vncFrameSink);
        try
        {
            if (ResolveCredentialDefinition(connection) is { } definition)
            {
                vaultSecret = _vault!.Reveal(definition.Id);
            }

            var server = await _vncClient.ConnectAsync(new RfbConnectionOptions
            {
                Endpoint = connection.Endpoint,
                Password = vaultSecret,
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
        finally
        {
            if (vaultSecret is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(vaultSecret);
            }
        }
    }

    private CredentialDefinition? ResolveCredentialDefinition(ConnectionProfile connection)
    {
        if (!ConnectionUsesIdentityCard(connection))
        {
            return null;
        }

        if (_vault is null || IsVaultLocked)
        {
            throw new InvalidOperationException("此連線需要身份卡，請先解鎖 Vault。");
        }

        var definitions = _vault.Credentials;
        var cards = definitions
            .Where(credential => credential.Kind is CredentialKind.UsernamePassword)
            .Select(credential => new IdentityCard
            {
                VaultId = credential.VaultId,
                CredentialId = credential.Id,
                Name = credential.Name,
                ProtocolId = credential.ProtocolScope,
                Username = credential.Username ?? string.Empty,
                Domain = credential.Domain,
            });
        var card = new ConnectionCredentialResolver().ResolveIdentityCard(connection, _folders, cards);
        if (card is null)
        {
            return null;
        }

        return definitions.First(credential =>
            credential.VaultId == card.VaultId && credential.Id == card.CredentialId);
    }

    private bool ConnectionUsesIdentityCard(ConnectionProfile connection)
    {
        if (connection.Credential.Kind is CredentialReferenceKind.IdentityCard)
        {
            return true;
        }

        if (connection.Credential.Kind is not CredentialReferenceKind.Inherited)
        {
            return false;
        }

        var folderId = connection.FolderId;
        var visited = new HashSet<FolderId>();
        while (folderId is { } currentId && visited.Add(currentId))
        {
            var folder = _folders.FirstOrDefault(candidate => candidate.Id == currentId);
            if (folder is null)
            {
                return false;
            }

            if (folder.Credential.Kind is CredentialReferenceKind.IdentityCard)
            {
                return true;
            }

            folderId = folder.ParentId;
        }

        return false;
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

    public string Endpoint => Profile.Endpoint.Port < 0
        ? Profile.Endpoint.Host
        : $"{Profile.Endpoint.Host}:{Profile.Endpoint.Port}";
}
