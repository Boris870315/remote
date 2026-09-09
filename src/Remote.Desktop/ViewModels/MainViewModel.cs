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
using Remote.Infrastructure.Import;
using Remote.Infrastructure.Diagnostics;
using Remote.Application.Vaults;
using Remote.Application.Credentials;
using Remote.Desktop.Protocols.Vnc;
using Remote.Protocols;

namespace Remote.Desktop.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private const string SshHostKeySetting = "ssh.host-key.sha256";
    private readonly IRemoteSessionService _sessionService;
    private readonly IRemoteProtocol _protocol;
    private readonly SessionLaunchPolicy _launchPolicy;
    private readonly SessionWorkspace _sessionWorkspace = new();
    private readonly RdpExternalSessionLauncher _rdpLauncher;
    private RfbClient? _vncClient;
    private AvaloniaRfbFrameSink? _vncFrameSink;
    private CancellationTokenSource? _vncCancellation;
    private readonly Dictionary<SessionId, VncSessionRuntime> _vncSessions = [];
    private SshTerminalSession? _sshSession;
    private CancellationTokenSource? _sshCancellation;
    private readonly Dictionary<SessionId, SshSessionRuntime> _sshSessions = [];
    private readonly Dictionary<SessionId, WebSessionRuntime> _webSessions = [];
    private LocalTerminalSession? _localTerminal;
    private CancellationTokenSource? _localTerminalCancellation;
    private readonly Dictionary<SessionId, LocalTerminalRuntime> _localTerminalSessions = [];
    private readonly TerminalOutputDecoder _terminalOutputDecoder = new();
    private readonly EncryptedWorkspaceRepository _workspaceRepository;
    private readonly EncryptedVaultArchiveService _vaultArchiveService;
    private readonly EncryptedWorkspaceBackupService _backupService = new();
    private readonly string _workspacePath;
    private readonly string _recoveryPath;
    private readonly LocalErrorLog _errorLog;
    private readonly string _backupDirectory;
    private readonly List<ConnectionFolder> _folders = [];
    private readonly List<ConnectionFolder> _pendingImportedFolders = [];
    private readonly List<ConnectionProfile> _pendingImportedConnections = [];
    private ConnectionProfile? _pendingSessionConnection;
    private SessionId? _pendingExistingSessionId;
    private ConnectionId? _editingConnectionId;
    private CredentialVault? _vault;
    private CredentialId? _editingCredentialId;
    private readonly RecoveryKeyEnvelopeService _recoveryKeyEnvelopeService = new();
    private string _activeMasterPassword = string.Empty;
    private VaultId _primaryVaultId = new(Guid.NewGuid());
    private VaultAutoLockController _autoLockController = new(new VaultLockSettings());
    private CancellationTokenSource? _secretRevealCancellation;
    private bool _loadingIdentitySecretForReveal;
    private bool _identitySecretWasEdited;

    public event Func<SessionId, RdpExternalLaunchRequest, Task>? EmbeddedRdpRequested;

    public event Action<string>? VncClipboardTextReceived;
    public event Func<SessionId, Task>? EmbeddedRdpCloseRequested;
    public event Func<SessionId, Uri, bool, Task>? EmbeddedWebRequested;
    public event Func<SessionId, Uri, Task>? EmbeddedWebNavigateRequested;
    public event Func<SessionId, Task>? EmbeddedWebCloseRequested;

    public ObservableCollection<SessionTabViewModel> SessionTabs { get; } = [];

    [ObservableProperty]
    private SessionTabViewModel? selectedSessionTab;

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
        _recoveryPath = $"{_workspacePath}.recovery";
        _errorLog = new LocalErrorLog(Path.Combine(Path.GetDirectoryName(_workspacePath)!, "Logs", "errors.jsonl"));
        _backupDirectory = Path.Combine(Path.GetDirectoryName(_workspacePath)!, "Backups");
        var productionFolder = new ConnectionFolder { Id = FolderId.New(), Name = "Production" };
        var labFolder = new ConnectionFolder { Id = FolderId.New(), Name = "Lab" };
        _folders.AddRange([productionFolder, labFolder]);
        RefreshFolderOptions();
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

    public ObservableCollection<CredentialDefinition> CompatibleVaultCredentials { get; } = [];

    public ObservableCollection<string> AuditEvents { get; } =
    [
        "Remote 已啟動",
        "本機 Workspace 已就緒",
    ];

    public ObservableCollection<ConnectionFolder> FolderOptions { get; } = [];

    public ObservableCollection<IdentityAssignmentTarget> IdentityAssignmentTargets { get; } = [];

    public ObservableCollection<string> SelectedIdentityUsages { get; } = [];

    public IReadOnlyList<string> IdentityProtocols { get; } = ["rdp", "vnc", "ssh2", "http", "https"];
    public IReadOnlyList<string> PasswordVisibilityOptions { get; } = ["5 秒", "10 秒", "永久顯示"];
    public IReadOnlyList<string> DisplayScaleOptions { get; } = ["適應視窗", "填滿視窗", "100%", "捲動"];
    public IReadOnlyList<RdpAudioMode> RdpAudioModeOptions { get; } = Enum.GetValues<RdpAudioMode>();
    public IReadOnlyList<RdpCertificatePolicy> RdpCertificatePolicyOptions { get; } = Enum.GetValues<RdpCertificatePolicy>();

    public ObservableCollection<ConnectionTreeDisplayItem> ConnectionTree { get; }

    [ObservableProperty]
    private ConnectionTreeDisplayItem? selectedTreeItem;

    [ObservableProperty]
    private ConnectionFolder? selectedFolder;

    [ObservableProperty]
    private string folderEditorName = string.Empty;

    [ObservableProperty]
    private ConnectionListItem? selectedConnection;

    [ObservableProperty]
    private IdentityAssignmentTarget? selectedIdentityAssignmentTarget;

    [ObservableProperty]
    private bool isViewOnly;

    [ObservableProperty]
    private string accessModeLabel = "Interactive";

    [ObservableProperty]
    private string statusDetail = "Ready";

    [ObservableProperty]
    private string sessionStatusLabel = "尚未開啟工作階段";

    [ObservableProperty]
    private string selectedDisplayScaleOption = "適應視窗";

    [ObservableProperty]
    private bool isSessionOpenChoiceVisible;

    [ObservableProperty]
    private string sessionOpenChoiceMessage = string.Empty;

    [ObservableProperty]
    private string quickConnectText = string.Empty;

    [ObservableProperty]
    private bool isEditingConnection;

    [ObservableProperty]
    private string editName = string.Empty;

    [ObservableProperty]
    private ConnectionFolder? editFolder;

    [ObservableProperty]
    private string newFolderName = string.Empty;

    [ObservableProperty]
    private string connectionSearchText = string.Empty;

    [ObservableProperty]
    private string editProtocol = "rdp";

    public IReadOnlyList<string> ConnectionProtocols { get; } = ["rdp", "vnc", "ssh2", "https", "http", "terminal"];

    public IReadOnlyList<string> CredentialSourceOptions { get; } =
    [
        "從資料夾繼承",
        "選擇 Vault 中的 ID Card",
        "新建並加密儲存到 Vault",
        "每次連線時輸入",
    ];

    public bool IsEditingRdp => string.Equals(EditProtocol, "rdp", StringComparison.OrdinalIgnoreCase);

    public bool IsEditingNetwork => !string.Equals(EditProtocol, "terminal", StringComparison.OrdinalIgnoreCase);

    public bool IsEditingTerminal => string.Equals(EditProtocol, "terminal", StringComparison.OrdinalIgnoreCase);

    public string ConnectionEditorTitle => _editingConnectionId is null ? "新增連線" : "編輯連線";

    [ObservableProperty]
    private string editCredentialSource = "從資料夾繼承";

    [ObservableProperty]
    private CredentialDefinition? editSelectedCredential;

    [ObservableProperty]
    private string editCredentialName = string.Empty;

    [ObservableProperty]
    private string editCredentialUsername = string.Empty;

    [ObservableProperty]
    private string editCredentialDomain = string.Empty;

    [ObservableProperty]
    private string editCredentialPassword = string.Empty;

    public bool IsSelectingExistingCredential => EditCredentialSource == "選擇 Vault 中的 ID Card";

    public bool IsCreatingCredential => EditCredentialSource == "新建並加密儲存到 Vault";

    public bool IsPromptingForCredential => EditCredentialSource == "每次連線時輸入";

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
    private bool editRedirectMicrophone;

    [ObservableProperty]
    private bool editRedirectCamera;

    [ObservableProperty]
    private bool editConnectAsAdministrator;

    [ObservableProperty]
    private RdpAudioMode editRdpAudioMode = RdpAudioMode.PlayLocally;

    [ObservableProperty]
    private RdpCertificatePolicy editRdpCertificatePolicy = RdpCertificatePolicy.RequireTrusted;

    [ObservableProperty]
    private string editShellPath = string.Empty;

    [ObservableProperty]
    private string editWorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [ObservableProperty]
    private bool editIsFavorite;

    [ObservableProperty]
    private string editTags = string.Empty;

    [ObservableProperty]
    private string connectionEditorError = string.Empty;

    [ObservableProperty]
    private WriteableBitmap? remoteFrame;

    [ObservableProperty]
    private string sessionUsername = string.Empty;

    [ObservableProperty]
    private string sessionPassword = string.Empty;

    [ObservableProperty]
    private string selectedPasswordVisibility = "10 秒";

    [ObservableProperty]
    private bool areEditableSecretsVisible;

    public char EditableSecretMask => AreEditableSecretsVisible ? '\0' : '●';

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
    private bool isRdpSessionActive;

    [ObservableProperty]
    private Uri? webSource;

    [ObservableProperty]
    private string webAddress = string.Empty;

    [ObservableProperty]
    private bool isVaultPanelOpen;

    [ObservableProperty]
    private bool isIdentityPanelOpen;

    [ObservableProperty]
    private bool isImportPanelOpen;

    [ObservableProperty]
    private bool isVaultLocked = true;

    [ObservableProperty]
    private string vaultMasterPassword = string.Empty;

    [ObservableProperty]
    private string vaultMessage = "Vault 已鎖定";

    [ObservableProperty]
    private string legacyImportPassword = string.Empty;

    [ObservableProperty]
    private string importMessage = "選擇 mRemoteNG confCons.xml 連線檔";

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
    private bool hideSensitiveContentFromCapture = true;

    [ObservableProperty]
    private bool clearClipboardAfterUse;

    [ObservableProperty]
    private bool isNotificationOpen;

    [ObservableProperty]
    private bool isSettingsPanelOpen;

    [ObservableProperty]
    private bool isAuditPanelOpen;

    [ObservableProperty]
    private string selectedMonitorOption = "目前視窗所在螢幕";

    [ObservableProperty]
    private string recoveryKey = string.Empty;

    [ObservableProperty]
    private string recoveryKeyInput = string.Empty;

    [ObservableProperty]
    private bool isDeleteConnectionConfirmationOpen;

    [ObservableProperty]
    private bool isDeleteFolderConfirmationOpen;

    [ObservableProperty]
    private string notificationMessage = string.Empty;

    [ObservableProperty]
    private bool isErrorDialogOpen;

    [ObservableProperty]
    private string errorDialogTitle = "發生重大錯誤";

    [ObservableProperty]
    private string errorDialogMessage = string.Empty;

    public string ErrorLogPath => _errorLog.Path;

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

    public bool HasSelectedConnection => SelectedConnection is not null;

    public bool ShowConnectionDetails => HasSelectedConnection && !IsEditingConnection;

    public bool HasSelectedFolder => SelectedFolder is not null;

    public bool IsVncSelected => string.Equals(SelectedConnection?.Profile.ProtocolId, "vnc", StringComparison.OrdinalIgnoreCase);

    public bool IsRdpSelected => string.Equals(SelectedConnection?.Profile.ProtocolId, "rdp", StringComparison.OrdinalIgnoreCase);

    public bool SelectedUsesAllMonitors =>
        SelectedConnection?.Profile.Display.MonitorSelection is MonitorSelection.All;

    public bool SelectedRedirectsClipboard => SelectedRdpSettings.RedirectClipboard;

    public bool SelectedRedirectsPrinters => SelectedRdpSettings.RedirectPrinters;

    public bool SelectedRedirectsDrives => SelectedRdpSettings.RedirectDrives;

    private RdpConnectionSettings SelectedRdpSettings => SelectedConnection is { Profile.ProtocolId: "rdp" } connection
        ? RdpConnectionSettings.FromProtocolSettings(connection.Profile.ProtocolSettings)
        : new RdpConnectionSettings();

    public bool RequiresSessionCredentialInput =>
        SelectedConnection is { } selected &&
        !ConnectionUsesIdentityCard(selected.Profile) &&
        selected.Profile.ProtocolId is "rdp" or "vnc" or "ssh2" or "http" or "https";

    public bool ShowSessionPlaceholder => RemoteFrame is null && !IsTerminalActive && !IsWebSessionActive && !IsRdpSessionActive;

    public bool IsVncSessionActive => _vncClient?.IsConnected is true;

    public bool IsSessionConnected =>
        RemoteFrame is not null || IsTerminalActive || IsWebSessionActive || IsRdpSessionActive;

    public ObservableCollection<string> MonitorOptions { get; } = ["螢幕 1（主螢幕）", "全部本機螢幕"];

    public void SetAvailableMonitorCount(int count)
    {
        count = Math.Max(1, count);
        var selectedIndex = SelectedConnection?.Profile.Display.MonitorIndex ?? 0;
        MonitorOptions.Clear();
        for (var index = 0; index < count; index++)
        {
            MonitorOptions.Add(index == 0 ? "螢幕 1（主螢幕）" : $"螢幕 {index + 1}");
        }
        MonitorOptions.Add("全部本機螢幕");
        SelectedMonitorOption = SelectedConnection?.Profile.Display.MonitorSelection is MonitorSelection.All
            ? "全部本機螢幕"
            : MonitorOptions[Math.Clamp(selectedIndex, 0, count - 1)];
    }

    public async Task UpdateSelectedRdpSettingsAsync(
        bool useAllMonitors,
        bool redirectClipboard,
        bool redirectPrinters,
        bool redirectDrives)
    {
        if (SelectedConnection is not { Profile.ProtocolId: "rdp" } selected)
        {
            return;
        }

        var current = RdpConnectionSettings.FromProtocolSettings(selected.Profile.ProtocolSettings);
        var monitorSelection = useAllMonitors ? MonitorSelection.All : MonitorSelection.Single;
        if (selected.Profile.Display.MonitorSelection == monitorSelection &&
            current.RedirectClipboard == redirectClipboard &&
            current.RedirectPrinters == redirectPrinters &&
            current.RedirectDrives == redirectDrives)
        {
            return;
        }

        var updatedSettings = current with
        {
            RedirectClipboard = redirectClipboard,
            RedirectPrinters = redirectPrinters,
            RedirectDrives = redirectDrives,
        };
        var updated = selected with
        {
            Profile = selected.Profile with
            {
                Display = selected.Profile.Display with
                {
                    MonitorSelection = monitorSelection,
                    MonitorIndex = useAllMonitors ? null : selected.Profile.Display.MonitorIndex ?? 0,
                },
                ProtocolSettings = updatedSettings.ToProtocolSettings(),
            },
        };
        Connections[Connections.IndexOf(selected)] = updated;
        SelectedConnection = updated;
        RebuildConnectionTree(updated.Profile.Id);
        if (!IsVaultLocked)
        {
            await SaveWorkspaceAsync();
        }

        SessionStatusLabel = "RDP 工作階段權限已儲存；新設定會套用到下一個 Session";
        AddAuditEvent($"變更 RDP 工作階段權限：{updated.Name}");
    }

    public string IdentityEditorTitle => _editingCredentialId is null ? "新增身份卡" : "編輯身份卡";

    public string IdentitySaveLabel => _editingCredentialId is null ? "加密儲存身份卡" : "儲存身份卡變更";

    public string IdentitySecretStatusLabel => NewIdentitySecret.Length > 0
        ? (AreEditableSecretsVisible ? "密碼已載入並暫時顯示" : "密碼已載入並遮罩")
        : _editingCredentialId is not null
            ? "✓ 已加密儲存密碼；按「顯示」可檢查"
            : "尚未輸入密碼";

    public bool IsEditingIdentity => _editingCredentialId is not null;

    public string SelectedCredentialSourceLabel => SelectedConnection?.Profile.Credential.Kind switch
    {
        CredentialReferenceKind.IdentityCard => "直接使用 Vault ID Card",
        CredentialReferenceKind.Inherited => "從資料夾繼承 ID Card",
        _ => "每次連線時輸入",
    };

    public string SelectedCredentialName => TryResolveSelectedCredential()?.Name ?? "尚未解析到 ID Card";

    public string SelectedCredentialUsername
    {
        get
        {
            var credential = TryResolveSelectedCredential();
            if (credential is null)
            {
                return IsVaultLocked ? "解鎖 Vault 後顯示" : "未儲存帳號密碼";
            }
            return string.IsNullOrWhiteSpace(credential.Domain)
                ? credential.Username ?? string.Empty
                : $"{credential.Domain}\\{credential.Username}";
        }
    }

    public Task<bool> SendVncKeyAsync(uint keySym, bool isDown) =>
        _vncClient?.SendKeyAsync(keySym, isDown) ?? Task.FromResult(false);

    public Task<bool> SendVncPointerAsync(byte buttonMask, ushort x, ushort y) =>
        _vncClient?.SendPointerAsync(buttonMask, x, y) ?? Task.FromResult(false);

    public Task<bool> SendVncClipboardTextAsync(string text) =>
        _vncClient?.SendClipboardTextAsync(text) ?? Task.FromResult(false);

    public void ResizeSelectedTerminal(double width, double height)
    {
        if (SelectedSessionTab is not { } tab) return;
        var dimensions = TerminalDimensions.FromPixels(width, height);
        try
        {
            if (_sshSessions.TryGetValue(tab.SessionId, out var ssh))
            {
                ssh.Session.Resize(
                    (uint)dimensions.Columns,
                    (uint)dimensions.Rows,
                    (uint)Math.Max(1, width),
                    (uint)Math.Max(1, height));
            }
            else if (_localTerminalSessions.TryGetValue(tab.SessionId, out var terminal))
            {
                terminal.Session.Resize(dimensions.Columns, dimensions.Rows);
            }
        }
        catch (InvalidOperationException)
        {
            // The process may exit between the session lookup and resize.
        }
    }

    public async Task ShutdownAsync()
    {
        foreach (var tab in SessionTabs.ToArray())
        {
            await CloseSessionTabAsync(tab);
        }
        await StopVncAsync();
        await StopSshAsync();
        await StopLocalTerminalAsync();
        LockVault();
    }

    partial void OnIsVaultLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(VaultStatusLabel));
        OnPropertyChanged(nameof(SelectedCredentialName));
        OnPropertyChanged(nameof(SelectedCredentialUsername));
    }

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
        OnPropertyChanged(nameof(IsEditingTerminal));
        RefreshCompatibleVaultCredentials();
        OnPropertyChanged(nameof(SelectedCredentialName));
        OnPropertyChanged(nameof(SelectedCredentialUsername));
    }

    partial void OnEditCredentialSourceChanged(string value)
    {
        OnPropertyChanged(nameof(IsSelectingExistingCredential));
        OnPropertyChanged(nameof(IsCreatingCredential));
        OnPropertyChanged(nameof(IsPromptingForCredential));
    }

    partial void OnConnectionSearchTextChanged(string value) => RebuildConnectionTree(
        SelectedConnection?.Profile.Id ?? default);

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
    private void OpenVaultPanel()
    {
        IsVaultPanelOpen = true;
    }

    [RelayCommand]
    private void OpenIdentityPanel()
    {
        RefreshIdentityAssignmentTargets();
        RefreshSelectedIdentityUsages();
        IsIdentityPanelOpen = true;
    }

    [RelayCommand]
    private void OpenImportPanel()
    {
        LegacyImportPassword = string.Empty;
        ImportMessage = IsVaultLocked
            ? "可以匯入連線與資料夾；Vault 鎖定時不匯入帳密"
            : "將匯入連線、資料夾、繼承設定與可用帳密";
        IsImportPanelOpen = true;
    }

    [RelayCommand]
    private void CloseImportPanel()
    {
        LegacyImportPassword = string.Empty;
        IsImportPanelOpen = false;
    }

    [RelayCommand]
    private void CloseIdentityPanel()
    {
        NewIdentitySecret = string.Empty;
        IsIdentityPanelOpen = false;
    }

    [RelayCommand]
    private void UnlockVaultFromIdentityPanel()
    {
        IsIdentityPanelOpen = false;
        IsVaultPanelOpen = true;
    }

    [RelayCommand]
    private void CloseVaultPanel()
    {
        VaultMasterPassword = string.Empty;
        RecoveryKeyInput = string.Empty;
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
            var workspaceExisted = File.Exists(_workspacePath);
            if (workspaceExisted)
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
                ApplyPendingConnectionImport();
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
            if (!workspaceExisted || _pendingImportedFolders.Count > 0 || _pendingImportedConnections.Count > 0)
            {
                await SaveWorkspaceAsync();
            }
            _pendingImportedFolders.Clear();
            _pendingImportedConnections.Clear();
        }
        catch (Exception exception) when (exception is WorkspaceUnlockException or InvalidDataException or NotSupportedException)
        {
            VaultMasterPassword = string.Empty;
            VaultMessage = exception.Message;
            await ReportMajorErrorAsync("Vault", "unlock-failed", $"Vault 解鎖失敗：{exception.Message}", exception);
        }
    }

    [RelayCommand]
    private void LockVault()
    {
        _secretRevealCancellation?.Cancel();
        AreEditableSecretsVisible = false;
        _vault?.Dispose();
        _vault = null;
        _activeMasterPassword = string.Empty;
        VaultMasterPassword = string.Empty;
        SessionPassword = string.Empty;
        NewIdentitySecret = string.Empty;
        RecoveryKey = string.Empty;
        RecoveryKeyInput = string.Empty;
        VaultCredentials.Clear();
        CompatibleVaultCredentials.Clear();
        EditSelectedCredential = null;
        IsVaultLocked = true;
        VaultMessage = "Vault 已鎖定；敏感內容已從執行階段清除";
    }

    [RelayCommand]
    private async Task RevealEditableSecretsAsync()
    {
        var identityCredentialId = _editingCredentialId ?? SelectedVaultCredential?.Id;
        if (identityCredentialId is { } credentialId &&
            NewIdentitySecret.Length == 0 &&
            _vault is not null &&
            !IsVaultLocked)
        {
            byte[]? revealed = null;
            try
            {
                revealed = _vault.Reveal(credentialId);
                _loadingIdentitySecretForReveal = true;
                NewIdentitySecret = Encoding.UTF8.GetString(revealed);
                VaultMessage = "已從加密 Vault 載入密碼";
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
            {
                VaultMessage = $"無法顯示密碼：{exception.Message}";
                NotificationMessage = VaultMessage;
                IsNotificationOpen = true;
                return;
            }
            finally
            {
                _loadingIdentitySecretForReveal = false;
                if (revealed is not null)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(revealed);
            }
        }

        _secretRevealCancellation?.Cancel();
        _secretRevealCancellation?.Dispose();
        _secretRevealCancellation = new CancellationTokenSource();
        AreEditableSecretsVisible = true;
        if (SelectedPasswordVisibility == "永久顯示") return;

        var delay = SelectedPasswordVisibility == "5 秒" ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(10);
        try
        {
            await Task.Delay(delay, _secretRevealCancellation.Token);
            AreEditableSecretsVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void BeginSecretReveal()
    {
        _secretRevealCancellation?.Cancel();
        AreEditableSecretsVisible = true;
    }

    public void EndSecretReveal() => AreEditableSecretsVisible = false;

    partial void OnAreEditableSecretsVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(EditableSecretMask));
        OnPropertyChanged(nameof(IdentitySecretStatusLabel));
    }

    partial void OnNewIdentitySecretChanged(string value)
    {
        if (!_loadingIdentitySecretForReveal)
        {
            _identitySecretWasEdited = true;
        }
        OnPropertyChanged(nameof(IdentitySecretStatusLabel));
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
        var isNew = _editingCredentialId is null;
        var usernameRequired = protocol is not "vnc";
        if (name.Length == 0 || (usernameRequired && username.Length == 0) ||
            (isNew && NewIdentitySecret.Length == 0) ||
            protocol is not ("rdp" or "vnc" or "ssh2" or "http" or "https"))
        {
            VaultMessage = usernameRequired
                ? "名稱、支援的協定、使用者名稱與密碼皆為必填"
                : "名稱、VNC 密碼皆為必填；使用者名稱為選填";
            return;
        }

        var definition = new CredentialDefinition
        {
            Id = _editingCredentialId ?? new CredentialId(Guid.NewGuid()),
            VaultId = _primaryVaultId,
            Name = name,
            Kind = CredentialKind.UsernamePassword,
            ProtocolScope = protocol,
            Username = username,
            Domain = string.IsNullOrWhiteSpace(NewIdentityDomain) ? null : NewIdentityDomain.Trim(),
        };
        byte[]? secret = NewIdentitySecret.Length == 0 || (!isNew && !_identitySecretWasEdited)
            ? null
            : Encoding.UTF8.GetBytes(NewIdentitySecret);
        try
        {
            if (_editingCredentialId is null)
            {
                _vault.Add(definition, secret!);
            }
            else if (secret is null)
            {
                _vault.UpdateDefinition(definition);
            }
            else
            {
                _vault.Update(definition, secret);
            }
            NewIdentitySecret = string.Empty;
            NewIdentityName = string.Empty;
            NewIdentityUsername = string.Empty;
            NewIdentityDomain = string.Empty;
            _editingCredentialId = null;
            _identitySecretWasEdited = false;
            OnPropertyChanged(nameof(IdentityEditorTitle));
            OnPropertyChanged(nameof(IdentitySaveLabel));
            OnPropertyChanged(nameof(IsEditingIdentity));
            RefreshVaultCredentials();
            await SaveWorkspaceAsync();
            VaultMessage = $"身份卡「{definition.Name}」已加密儲存";
            NotificationMessage = $"身份卡「{definition.Name}」的帳號與密碼已加密儲存成功。";
            IsNotificationOpen = true;
            AddAuditEvent($"身份卡已儲存：{definition.Name}");
        }
        finally
        {
            if (secret is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
            }
        }
    }

    public async Task ImportMRemoteNgAsync(string path)
    {
        try
        {
            var xml = await File.ReadAllTextAsync(path);
            using var result = new MRemoteNgXmlImporter().Import(xml, LegacyImportPassword, _primaryVaultId);
            var canImportCredentials = _vault is not null && !IsVaultLocked;
            if (canImportCredentials)
            {
                foreach (var imported in result.IdentityCards)
                {
                    _vault!.Add(imported.Definition, imported.Secret);
                }
            }

            var importedFolders = canImportCredentials
                ? result.Folders
                : result.Folders.Select(folder => folder with
                {
                    Credential = ConnectionCredentialReference.None,
                    ProtocolCredentials = new Dictionary<string, ConnectionCredentialReference>(StringComparer.OrdinalIgnoreCase),
                }).ToArray();
            var importedConnections = canImportCredentials
                ? result.Connections
                : result.Connections.Select(profile => profile with
                {
                    Credential = ConnectionCredentialReference.None,
                }).ToArray();
            ApplyConnectionImport(importedFolders, importedConnections);
            if (!canImportCredentials)
            {
                _pendingImportedFolders.AddRange(importedFolders);
                _pendingImportedConnections.AddRange(importedConnections);
            }

            LegacyImportPassword = string.Empty;
            RefreshVaultCredentials();
            if (canImportCredentials)
            {
                await SaveWorkspaceAsync();
            }
            ImportMessage = canImportCredentials
                ? $"匯入完成：{result.Folders.Count} 個資料夾、{result.Connections.Count} 個連線；已安全轉換 {result.IdentityCards.Count} 組帳密"
                : $"已匯入 {result.Folders.Count} 個資料夾、{result.Connections.Count} 個連線。Vault 鎖定，帳密未匯入；解鎖後會儲存連線資料。";
            if (result.Warnings.Count > 0)
            {
                ImportMessage += $"另有 {result.Warnings.Count} 項不支援設定。";
            }
            AddAuditEvent($"mRemoteNG 連線匯入：{result.Connections.Count} 個連線");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Xml.XmlException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            LegacyImportPassword = string.Empty;
            ImportMessage = $"mRemoteNG 匯入失敗：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task AssignIdentityCardAsync()
    {
        if (SelectedIdentityAssignmentTarget is null ||
            SelectedVaultCredential is null || _vault is null || IsVaultLocked)
        {
            VaultMessage = "請選取身份卡及套用目標";
            return;
        }

        var targetConnection = SelectedIdentityAssignmentTarget.ConnectionId is { } connectionId
            ? Connections.FirstOrDefault(item => item.Profile.Id == connectionId)
            : null;
        var targetFolder = SelectedIdentityAssignmentTarget.FolderId is { } folderId
            ? _folders.FirstOrDefault(item => item.Id == folderId)
            : null;
        if (targetConnection is not null && !string.Equals(
                SelectedVaultCredential.ProtocolScope,
                targetConnection.Profile.ProtocolId,
                StringComparison.OrdinalIgnoreCase))
        {
            VaultMessage = $"此身份卡僅供 {SelectedVaultCredential.ProtocolScope.ToUpperInvariant()} 使用";
            return;
        }

        if (targetFolder is { } selectedFolder)
        {
            var folderIndex = _folders.FindIndex(folder => folder.Id == selectedFolder.Id);
            var protocolCredentials = selectedFolder.ProtocolCredentials
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            protocolCredentials[SelectedVaultCredential.ProtocolScope] =
                ConnectionCredentialReference.IdentityCard(
                    SelectedVaultCredential.VaultId,
                    SelectedVaultCredential.Id);
            var updatedFolder = selectedFolder with
            {
                ProtocolCredentials = protocolCredentials,
            };
            _folders[folderIndex] = updatedFolder;
            RefreshFolderOptions();
            RebuildConnectionTree(default);
            SelectedTreeItem = FindFolderTreeItem(ConnectionTree, updatedFolder.Id);
            SelectedIdentityAssignmentTarget = IdentityAssignmentTargets.FirstOrDefault(target => target.FolderId == updatedFolder.Id);
            await SaveWorkspaceAsync();
            VaultMessage = $"已將「{SelectedVaultCredential.Name}」設為資料夾 {updatedFolder.Name} 的 {SelectedVaultCredential.ProtocolScope.ToUpperInvariant()} 身份卡";
            return;
        }

        if (targetConnection is null)
        {
            VaultMessage = "選取的套用目標已不存在，請重新選擇";
            RefreshIdentityAssignmentTargets();
            return;
        }

        var selectedConnection = targetConnection;
        var index = Connections.IndexOf(selectedConnection);
        var updated = selectedConnection with
        {
            Profile = selectedConnection.Profile with
            {
                Credential = ConnectionCredentialReference.IdentityCard(
                    SelectedVaultCredential.VaultId,
                    SelectedVaultCredential.Id),
            },
            IdentityScope = $"{SelectedVaultCredential.ProtocolScope.ToUpperInvariant()} only",
        };
        Connections[index] = updated;
        RebuildConnectionTree(updated.Profile.Id);
        SelectedIdentityAssignmentTarget = IdentityAssignmentTargets.FirstOrDefault(target => target.ConnectionId == updated.Profile.Id);
        await SaveWorkspaceAsync();
        var resolved = ResolveCredentialDefinition(updated.Profile);
        VaultMessage = resolved?.Id == SelectedVaultCredential.Id
            ? $"已驗證：{updated.Name} 連線時會自動使用「{resolved.Name}」"
            : $"身份卡已指派，但解析驗證失敗，請重新指派";
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
    private void DismissErrorDialog() => IsErrorDialogOpen = false;

    [RelayCommand]
    private void OpenSettingsPanel() => IsSettingsPanelOpen = true;

    [RelayCommand]
    private async Task CloseSettingsPanelAsync()
    {
        RecoveryKey = string.Empty;
        IsSettingsPanelOpen = false;
        if (!IsVaultLocked && _activeMasterPassword.Length > 0)
        {
            await SaveWorkspaceAsync();
            VaultMessage = "設定已儲存到加密 Workspace";
        }
    }

    [RelayCommand]
    private void OpenAuditPanel() => IsAuditPanelOpen = true;

    [RelayCommand]
    private void CloseAuditPanel() => IsAuditPanelOpen = false;

    [RelayCommand]
    private async Task ApplyMonitorSelectionAsync()
    {
        if (SelectedConnection is not { } selected)
        {
            return;
        }

        var all = SelectedMonitorOption == "全部本機螢幕";
        var monitorIndex = all ? 0 : Math.Max(0, MonitorOptions.IndexOf(SelectedMonitorOption));
        var updated = selected with
        {
            Profile = selected.Profile with
            {
                Display = selected.Profile.Display with
                {
                    MonitorSelection = all ? MonitorSelection.All : MonitorSelection.Single,
                    MonitorIndex = all ? null : monitorIndex,
                },
            },
        };
        Connections[Connections.IndexOf(selected)] = updated;
        SelectedConnection = updated;
        RebuildConnectionTree(updated.Profile.Id);
        if (!IsVaultLocked)
        {
            await SaveWorkspaceAsync();
        }
        SessionStatusLabel = $"顯示範圍已設定為 {SelectedMonitorOption}";
        AddAuditEvent($"變更顯示範圍：{updated.Name} · {SelectedMonitorOption}");
        if (IsRdpSessionActive && SelectedSessionTab is { } activeTab &&
            activeTab.Connection.Id == updated.Profile.Id)
        {
            await CloseSessionTabAsync(activeTab);
            await LaunchNewConnectionAsync(updated.Profile);
        }
    }

    [RelayCommand]
    private async Task ApplyDisplayScaleAsync()
    {
        if (SelectedConnection is not { } selected) return;
        var scaleMode = SelectedDisplayScaleOption switch
        {
            "填滿視窗" => DisplayScaleMode.Fill,
            "100%" => DisplayScaleMode.ActualSize,
            "捲動" => DisplayScaleMode.Scroll,
            _ => DisplayScaleMode.Fit,
        };
        if (selected.Profile.Display.ScaleMode == scaleMode) return;

        var updated = selected with
        {
            Profile = selected.Profile with
            {
                Display = selected.Profile.Display with { ScaleMode = scaleMode },
            },
        };
        Connections[Connections.IndexOf(selected)] = updated;
        SelectedConnection = updated;
        RebuildConnectionTree(updated.Profile.Id);
        if (!IsVaultLocked) await SaveWorkspaceAsync();
        SessionStatusLabel = $"顯示縮放已設定為 {SelectedDisplayScaleOption}";
    }

    [RelayCommand]
    private void BeginEditIdentityCard()
    {
        if (SelectedVaultCredential is not { } credential)
        {
            VaultMessage = "請先選取要編輯的身份卡";
            return;
        }

        _editingCredentialId = credential.Id;
        NewIdentityName = credential.Name;
        NewIdentityProtocol = credential.ProtocolScope;
        NewIdentityUsername = credential.Username ?? string.Empty;
        NewIdentityDomain = credential.Domain ?? string.Empty;
        var revealed = _vault!.Reveal(credential.Id);
        try
        {
            _loadingIdentitySecretForReveal = true;
            NewIdentitySecret = Encoding.UTF8.GetString(revealed);
        }
        finally
        {
            _loadingIdentitySecretForReveal = false;
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(revealed);
        }
        _identitySecretWasEdited = false;
        OnPropertyChanged(nameof(IdentityEditorTitle));
        OnPropertyChanged(nameof(IdentitySaveLabel));
        OnPropertyChanged(nameof(IsEditingIdentity));
        VaultMessage = "✓ 此身份卡已有加密密碼；按「顯示」可檢查。留空儲存會保留原密碼。";
    }

    [RelayCommand]
    private void CancelIdentityCardEdit()
    {
        _editingCredentialId = null;
        NewIdentityName = string.Empty;
        NewIdentityUsername = string.Empty;
        NewIdentityDomain = string.Empty;
        NewIdentitySecret = string.Empty;
        _identitySecretWasEdited = false;
        OnPropertyChanged(nameof(IdentityEditorTitle));
        OnPropertyChanged(nameof(IdentitySaveLabel));
        OnPropertyChanged(nameof(IsEditingIdentity));
    }

    [RelayCommand]
    private async Task GenerateRecoveryKeyAsync()
    {
        if (IsVaultLocked || _activeMasterPassword.Length == 0)
        {
            VaultMessage = "請先使用主密碼解鎖 Vault，再建立 Recovery Key";
            return;
        }

        await SaveWorkspaceAsync();
        RecoveryKey = await _recoveryKeyEnvelopeService.CreateAsync(_recoveryPath, _activeMasterPassword);
        NotificationMessage = "新的 Recovery Key 已建立。請立即離線保存；關閉此畫面後不會再次顯示。舊的 Recovery Key 已失效。";
        IsNotificationOpen = true;
        VaultMessage = "Recovery Key 已建立並綁定目前 Workspace";
        AddAuditEvent("已產生新的 Recovery Key");
    }

    [RelayCommand]
    private async Task UnlockWithRecoveryKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(RecoveryKeyInput))
        {
            VaultMessage = "請輸入 Recovery Key";
            return;
        }

        try
        {
            VaultMasterPassword = await _recoveryKeyEnvelopeService.RecoverMasterPasswordAsync(
                _recoveryPath,
                RecoveryKeyInput);
            RecoveryKeyInput = string.Empty;
            await UnlockVaultAsync();
            if (!IsVaultLocked)
            {
                VaultMessage = "已使用 Recovery Key 解鎖 Vault";
                AddAuditEvent("Vault 已使用 Recovery Key 解鎖");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            VaultMasterPassword = string.Empty;
            RecoveryKeyInput = string.Empty;
            VaultMessage = exception.Message;
        }
    }

    [RelayCommand]
    private void RequestDeleteConnection()
    {
        if (SelectedConnection is null)
        {
            SessionStatusLabel = "請先選取要刪除的連線";
            return;
        }

        IsDeleteConnectionConfirmationOpen = true;
    }

    [RelayCommand]
    private void CancelDeleteConnection() => IsDeleteConnectionConfirmationOpen = false;

    [RelayCommand]
    private async Task ConfirmDeleteConnectionAsync()
    {
        if (SelectedConnection is not { } selected)
        {
            IsDeleteConnectionConfirmationOpen = false;
            return;
        }

        var deletedName = selected.Name;
        Connections.Remove(selected);
        SelectedConnection = Connections.FirstOrDefault();
        RebuildConnectionTree(SelectedConnection?.Profile.Id ?? default);
        SelectedTreeItem = SelectedConnection is { } next
            ? FindConnectionTreeItem(ConnectionTree, next.Profile.Id)
            : null;
        IsDeleteConnectionConfirmationOpen = false;
        if (!IsVaultLocked)
        {
            await SaveWorkspaceAsync();
        }

        NotificationMessage = $"連線「{deletedName}」已刪除。";
        IsNotificationOpen = true;
        AddAuditEvent($"連線已刪除：{deletedName}");
    }

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

    public async Task RestoreBackupAsync(string backupPath)
    {
        if (_vault is null || IsVaultLocked || _activeMasterPassword.Length == 0)
        {
            VaultMessage = "請先解鎖 Vault，再還原備份";
            return;
        }

        try
        {
            var restoredDocument = await _workspaceRepository.LoadAsync(backupPath, _activeMasterPassword);
            CredentialVault restoredVault;
            if (restoredDocument.EncryptedPrimaryVault is { Length: > 0 } archive)
            {
                try
                {
                    restoredVault = _vaultArchiveService.Import(archive, _activeMasterPassword);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(archive);
                }
            }
            else
            {
                restoredVault = new CredentialVault();
            }

            if (File.Exists(_workspacePath))
            {
                await _backupService.CreateAsync(_workspacePath, _backupDirectory, DateTimeOffset.Now);
            }
            await _backupService.RestoreAsync(backupPath, _workspacePath);
            _vault.Dispose();
            _vault = restoredVault;
            LoadWorkspaceDocument(restoredDocument);
            RefreshVaultCredentials();
            VaultMessage = "加密備份已還原；原 Workspace 已先建立安全副本";
            NotificationMessage = VaultMessage;
            IsNotificationOpen = true;
            AddAuditEvent("Workspace 已由本機加密備份還原");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or WorkspaceUnlockException or NotSupportedException)
        {
            VaultMessage = $"備份還原失敗：{exception.Message}";
        }
    }

    partial void OnSelectedConnectionChanged(ConnectionListItem? value)
    {
        IsViewOnly = value?.Profile.DefaultAccessMode is SessionAccessMode.ViewOnly;
        ExpectedHostKey = value?.Profile.ProtocolSettings.Get(SshHostKeySetting) ?? string.Empty;
        TrustUnknownHostKey = false;
        SelectedMonitorOption = value?.Profile.Display.MonitorSelection is MonitorSelection.All
            ? "全部本機螢幕"
            : MonitorOptions[Math.Clamp(value?.Profile.Display.MonitorIndex ?? 0, 0, Math.Max(0, MonitorOptions.Count - 2))];
        SelectedDisplayScaleOption = value?.Profile.Display.ScaleMode switch
        {
            DisplayScaleMode.Fill => "填滿視窗",
            DisplayScaleMode.ActualSize => "100%",
            DisplayScaleMode.Scroll => "捲動",
            _ => "適應視窗",
        };
        OnPropertyChanged(nameof(IsSshSelected));
        OnPropertyChanged(nameof(IsVncSelected));
        OnPropertyChanged(nameof(IsRdpSelected));
        OnPropertyChanged(nameof(SelectedUsesAllMonitors));
        OnPropertyChanged(nameof(SelectedRedirectsClipboard));
        OnPropertyChanged(nameof(SelectedRedirectsPrinters));
        OnPropertyChanged(nameof(SelectedRedirectsDrives));
        OnPropertyChanged(nameof(HasSelectedConnection));
        OnPropertyChanged(nameof(ShowConnectionDetails));
        OnPropertyChanged(nameof(RequiresSessionCredentialInput));
        OnPropertyChanged(nameof(SelectedCredentialSourceLabel));
        OnPropertyChanged(nameof(SelectedCredentialName));
        OnPropertyChanged(nameof(SelectedCredentialUsername));
    }

    partial void OnSelectedVaultCredentialChanged(CredentialDefinition? value) =>
        RefreshSelectedIdentityUsages();

    partial void OnSelectedTreeItemChanged(ConnectionTreeDisplayItem? value)
    {
        if (value?.Connection is { } connection)
        {
            SelectedFolder = null;
            SelectedConnection = Connections.FirstOrDefault(item => item.Profile.Id == connection.Id);
            return;
        }

        SelectedFolder = value?.Folder;
        if (SelectedFolder is not null)
        {
            IsEditingConnection = false;
            SelectedConnection = null;
        }
    }

    partial void OnIsEditingConnectionChanged(bool value) =>
        OnPropertyChanged(nameof(ShowConnectionDetails));

    partial void OnSelectedFolderChanged(ConnectionFolder? value)
    {
        FolderEditorName = value?.Name ?? string.Empty;
        OnPropertyChanged(nameof(HasSelectedFolder));
    }

    partial void OnRemoteFrameChanged(WriteableBitmap? value)
    {
        OnPropertyChanged(nameof(ShowSessionPlaceholder));
        OnPropertyChanged(nameof(IsSessionConnected));
    }

    partial void OnIsTerminalActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSessionPlaceholder));
        OnPropertyChanged(nameof(IsSessionConnected));
    }

    partial void OnIsWebSessionActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSessionPlaceholder));
        OnPropertyChanged(nameof(IsSessionConnected));
    }

    partial void OnIsRdpSessionActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSessionPlaceholder));
        OnPropertyChanged(nameof(IsSessionConnected));
    }

    public string RuntimeStatus => _sessionService.GetStatus().State;

    public string ProtocolName => _protocol.Descriptor.DisplayName;

    partial void OnIsViewOnlyChanged(bool value) => UpdateAccessMode();

    [RelayCommand]
    private void BeginNewConnection()
    {
        _editingConnectionId = null;
        OnPropertyChanged(nameof(ConnectionEditorTitle));
        EditName = string.Empty;
        EditFolder = SelectedFolder;
        EditProtocol = "rdp";
        EditHost = string.Empty;
        EditPort = "3389";
        EditGateway = string.Empty;
        EditUseAllMonitors = false;
        EditViewOnly = false;
        EditRedirectClipboard = true;
        EditRedirectPrinters = false;
        EditRedirectDrives = false;
        EditRedirectMicrophone = false;
        EditRedirectCamera = false;
        EditConnectAsAdministrator = false;
        EditRdpAudioMode = RdpAudioMode.PlayLocally;
        EditRdpCertificatePolicy = RdpCertificatePolicy.RequireTrusted;
        EditShellPath = string.Empty;
        EditWorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        EditIsFavorite = false;
        EditTags = string.Empty;
        EditCredentialSource = "從資料夾繼承";
        EditSelectedCredential = null;
        ClearConnectionCredentialEditor();
        RefreshCompatibleVaultCredentials();
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
        EditFolder = profile.FolderId is { } folderId
            ? _folders.FirstOrDefault(folder => folder.Id == folderId)
            : null;
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
        EditRedirectMicrophone = settings.RedirectMicrophone;
        EditRedirectCamera = settings.RedirectCamera;
        EditConnectAsAdministrator = settings.ConnectAsAdministrator;
        EditRdpAudioMode = settings.AudioMode;
        EditRdpCertificatePolicy = settings.CertificatePolicy;
        var terminalSettings = LocalTerminalOptions.FromProtocolSettings(profile.ProtocolSettings);
        EditShellPath = terminalSettings.ShellPath ?? string.Empty;
        EditWorkingDirectory = terminalSettings.WorkingDirectory;
        EditIsFavorite = profile.IsFavorite;
        EditTags = string.Join(", ", profile.Tags);
        RefreshCompatibleVaultCredentials();
        EditCredentialSource = profile.Credential.Kind switch
        {
            CredentialReferenceKind.IdentityCard => "選擇 Vault 中的 ID Card",
            CredentialReferenceKind.None => "每次連線時輸入",
            _ => "從資料夾繼承",
        };
        EditSelectedCredential = profile.Credential.CredentialId is { } credentialId
            ? CompatibleVaultCredentials.FirstOrDefault(item => item.Id == credentialId)
            : null;
        ClearConnectionCredentialEditor();
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
        EditCredentialPassword = string.Empty;
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

        LocalTerminalOptions? terminalOptions = null;
        if (IsEditingTerminal)
        {
            if (string.IsNullOrWhiteSpace(EditWorkingDirectory) || !Directory.Exists(EditWorkingDirectory.Trim()))
            {
                ConnectionEditorError = "Terminal 啟動目錄不存在";
                return;
            }
            try
            {
                _ = LocalTerminalSession.ResolveShell(string.IsNullOrWhiteSpace(EditShellPath) ? null : EditShellPath.Trim());
            }
            catch (Exception exception) when (exception is IOException or ArgumentException)
            {
                ConnectionEditorError = exception.Message;
                return;
            }
            terminalOptions = new LocalTerminalOptions
            {
                ShellPath = string.IsNullOrWhiteSpace(EditShellPath) ? null : Path.GetFullPath(EditShellPath.Trim()),
                WorkingDirectory = Path.GetFullPath(EditWorkingDirectory.Trim()),
                AccessMode = EditViewOnly ? SessionAccessMode.ViewOnly : SessionAccessMode.Interactive,
            };
        }

        var settings = new RdpConnectionSettings
        {
            GatewayHost = string.IsNullOrWhiteSpace(EditGateway) ? null : EditGateway.Trim(),
            RedirectClipboard = EditRedirectClipboard,
            RedirectPrinters = EditRedirectPrinters,
            RedirectDrives = EditRedirectDrives,
            RedirectMicrophone = EditRedirectMicrophone,
            RedirectCamera = EditRedirectCamera,
            ConnectAsAdministrator = EditConnectAsAdministrator,
            AudioMode = EditRdpAudioMode,
            CertificatePolicy = EditRdpCertificatePolicy,
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

        var credentialReference = ConnectionCredentialReference.Inherited;
        if (IsSelectingExistingCredential)
        {
            if (_vault is null || IsVaultLocked)
            {
                ConnectionEditorError = "請先解鎖 Vault，再選擇 ID Card";
                return;
            }
            if (EditSelectedCredential is not { } selectedCredential ||
                !string.Equals(selectedCredential.ProtocolScope, EditProtocol, StringComparison.OrdinalIgnoreCase))
            {
                ConnectionEditorError = $"請選擇一張可供 {EditProtocol.ToUpperInvariant()} 使用的 ID Card";
                return;
            }
            credentialReference = ConnectionCredentialReference.IdentityCard(
                selectedCredential.VaultId,
                selectedCredential.Id);
        }
        else if (IsCreatingCredential)
        {
            if (_vault is null || IsVaultLocked)
            {
                ConnectionEditorError = "請先解鎖 Vault，再建立並儲存帳密";
                return;
            }
            if (string.IsNullOrWhiteSpace(EditCredentialUsername) || EditCredentialPassword.Length == 0)
            {
                ConnectionEditorError = "使用者名稱與密碼為必填欄位";
                return;
            }

            var definition = new CredentialDefinition
            {
                Id = new CredentialId(Guid.NewGuid()),
                VaultId = _primaryVaultId,
                Name = string.IsNullOrWhiteSpace(EditCredentialName)
                    ? $"{name} 登入"
                    : EditCredentialName.Trim(),
                Kind = CredentialKind.UsernamePassword,
                ProtocolScope = EditProtocol,
                Username = EditCredentialUsername.Trim(),
                Domain = string.IsNullOrWhiteSpace(EditCredentialDomain)
                    ? null
                    : EditCredentialDomain.Trim(),
            };
            var secret = Encoding.UTF8.GetBytes(EditCredentialPassword);
            try
            {
                _vault.Add(definition, secret);
                credentialReference = ConnectionCredentialReference.IdentityCard(definition.VaultId, definition.Id);
                RefreshVaultCredentials();
                EditSelectedCredential = definition;
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
                EditCredentialPassword = string.Empty;
            }
        }
        else if (IsPromptingForCredential)
        {
            credentialReference = ConnectionCredentialReference.None;
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
            FolderId = EditFolder?.Id,
            Credential = credentialReference,
            DefaultAccessMode = EditViewOnly ? SessionAccessMode.ViewOnly : SessionAccessMode.Interactive,
            Display = new DisplayPreferences
            {
                MonitorSelection = EditUseAllMonitors ? MonitorSelection.All : MonitorSelection.Single,
            },
            ProtocolSettings = IsEditingRdp
                ? settings.ToProtocolSettings()
                : terminalOptions?.ToProtocolSettings() ?? new ProtocolSettings(),
            IsFavorite = EditIsFavorite,
            Tags = EditTags.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
        };
        var item = new ConnectionListItem(
            profile,
            credentialReference.Kind switch
            {
                CredentialReferenceKind.Inherited => "Inherited ID Card",
                CredentialReferenceKind.IdentityCard => $"{EditProtocol.ToUpperInvariant()} ID Card",
                _ => "Prompt every time",
            },
            profile.IsFavorite);
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
        ClearConnectionCredentialEditor();
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
    private async Task AddFolderAsync()
    {
        var name = NewFolderName.Trim();
        if (name.Length == 0)
        {
            SessionStatusLabel = "請輸入資料夾名稱";
            return;
        }

        var folder = new ConnectionFolder
        {
            Id = FolderId.New(),
            Name = name,
            ParentId = SelectedFolder?.Id,
        };
        _folders.Add(folder);
        RefreshFolderOptions();
        RebuildConnectionTree(SelectedConnection?.Profile.Id ?? default);
        SelectedTreeItem = FindFolderTreeItem(ConnectionTree, folder.Id);
        NewFolderName = string.Empty;
        if (!IsVaultLocked)
        {
            await SaveWorkspaceAsync();
            SessionStatusLabel = $"已建立資料夾 {name}";
        }
        else
        {
            SessionStatusLabel = $"已建立資料夾 {name}；解鎖 Vault 後才能寫入磁碟";
        }
    }

    [RelayCommand]
    private async Task RenameSelectedFolderAsync()
    {
        if (SelectedFolder is not { } selected)
        {
            SessionStatusLabel = "請先選取資料夾";
            return;
        }

        var name = FolderEditorName.Trim();
        if (name.Length == 0)
        {
            SessionStatusLabel = "資料夾名稱不可空白";
            return;
        }

        var index = _folders.FindIndex(folder => folder.Id == selected.Id);
        if (index < 0)
        {
            SessionStatusLabel = "資料夾已不存在";
            return;
        }

        var updated = selected with { Name = name };
        _folders[index] = updated;
        RefreshFolderOptions();
        RebuildConnectionTree(default);
        SelectedTreeItem = FindFolderTreeItem(ConnectionTree, updated.Id);
        if (!IsVaultLocked)
        {
            await SaveWorkspaceAsync();
        }
        SessionStatusLabel = $"資料夾已重新命名為 {name}";
    }

    [RelayCommand]
    private void RequestDeleteFolder()
    {
        if (SelectedFolder is not null)
        {
            IsDeleteFolderConfirmationOpen = true;
        }
    }

    [RelayCommand]
    private void CancelDeleteFolder() => IsDeleteFolderConfirmationOpen = false;

    [RelayCommand]
    private async Task ConfirmDeleteFolderAsync()
    {
        if (SelectedFolder is not { } selected)
        {
            IsDeleteFolderConfirmationOpen = false;
            return;
        }

        var deletedFolderIds = new HashSet<FolderId> { selected.Id };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var folder in _folders)
            {
                if (folder.ParentId is { } parentId && deletedFolderIds.Contains(parentId) && deletedFolderIds.Add(folder.Id))
                {
                    changed = true;
                }
            }
        }

        var deletedConnectionCount = 0;
        foreach (var connection in Connections.Where(item =>
                     item.Profile.FolderId is { } folderId && deletedFolderIds.Contains(folderId)).ToArray())
        {
            Connections.Remove(connection);
            deletedConnectionCount++;
        }
        _folders.RemoveAll(folder => deletedFolderIds.Contains(folder.Id));
        RefreshFolderOptions();
        SelectedFolder = null;
        SelectedTreeItem = null;
        RebuildConnectionTree(default);
        IsDeleteFolderConfirmationOpen = false;
        if (!IsVaultLocked)
        {
            await SaveWorkspaceAsync();
        }
        NotificationMessage = $"已刪除資料夾「{selected.Name}」、{deletedFolderIds.Count - 1} 個子資料夾與 {deletedConnectionCount} 個連線；ID Card 保留。";
        IsNotificationOpen = true;
        AddAuditEvent($"資料夾已刪除：{selected.Name}");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
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

    public Task OpenConnectionInNewTabAsync(ConnectionProfile connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var sessionConnection = connection with
        {
            DefaultAccessMode = IsViewOnly ? SessionAccessMode.ViewOnly : SessionAccessMode.Interactive,
        };
        return LaunchNewConnectionAsync(sessionConnection);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
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
            _pendingSessionConnection = connection;
            _pendingExistingSessionId = request.ExistingSessions[0].Id;
            SessionOpenChoiceMessage = $"「{connection.Name}」已有工作階段。要切換至現有分頁，還是另開一個工作階段？";
            IsSessionOpenChoiceVisible = true;
            return;
        }

        await LaunchNewConnectionAsync(connection);
    }

    private async Task LaunchNewConnectionAsync(ConnectionProfile connection)
    {

        var session = _sessionWorkspace.OpenNew(connection);
        var connectionSessionNumber = SessionTabs.Count(item => item.Connection.Id == connection.Id) + 1;
        var tabTitle = connectionSessionNumber == 1
            ? connection.Name
            : $"{connection.Name} · {connectionSessionNumber}";
        var tab = new SessionTabViewModel(session.Id, connection, tabTitle);
        SessionTabs.Add(tab);
        SelectSessionTab(tab);
        if (string.Equals(connection.ProtocolId, "vnc", StringComparison.OrdinalIgnoreCase))
        {
            await LaunchVncAsync(connection, session.Id);
            return;
        }

        if (string.Equals(connection.ProtocolId, "ssh2", StringComparison.OrdinalIgnoreCase))
        {
            await LaunchSshAsync(connection, session.Id);
            return;
        }

        if (connection.ProtocolId is "http" or "https")
        {
            WebSource = WebNavigationPolicy.ParseHttpEndpoint(connection.Endpoint.ToString());
            WebAddress = WebSource.ToString();
            _webSessions.Add(session.Id, new WebSessionRuntime(WebSource, connection.DefaultAccessMode is SessionAccessMode.ViewOnly));
            if (EmbeddedWebRequested is { } openWeb)
                await openWeb(session.Id, WebSource, connection.DefaultAccessMode is SessionAccessMode.ViewOnly);
            IsWebSessionActive = true;
            SetSessionState(session.Id, SessionState.Connected);
            SessionStatusLabel = $"{connection.ProtocolId.ToUpperInvariant()} 已載入 · {connection.Endpoint.Host}";
            return;
        }

        if (string.Equals(connection.ProtocolId, "terminal", StringComparison.OrdinalIgnoreCase))
        {
            await LaunchLocalTerminalAsync(connection, session.Id);
            return;
        }

        if (!string.Equals(connection.ProtocolId, "rdp", StringComparison.OrdinalIgnoreCase))
        {
            SessionStatusLabel = $"{session.State} · 等待 {connection.ProtocolId.ToUpperInvariant()} Adapter Host";
            return;
        }

        byte[]? rdpSecret = null;
        try
        {
            string? username = null;
            string? domain = null;
            if (ResolveCredentialDefinition(connection) is { } definition)
            {
                username = string.IsNullOrEmpty(definition.Username) ? null : definition.Username;
                domain = string.IsNullOrEmpty(definition.Domain) ? null : definition.Domain;
                rdpSecret = _vault!.Reveal(definition.Id);
            }
            else
            {
                username = string.IsNullOrEmpty(SessionUsername) ? null : SessionUsername;
                rdpSecret = string.IsNullOrEmpty(SessionPassword)
                    ? null
                    : Encoding.UTF8.GetBytes(SessionPassword);
            }

            var launchRequest = new RdpExternalLaunchRequest
            {
                Endpoint = connection.Endpoint,
                Username = username,
                Domain = domain,
                PasswordUtf8 = rdpSecret,
                AccessMode = connection.DefaultAccessMode,
                Display = connection.Display,
                Settings = RdpConnectionSettings.FromProtocolSettings(connection.ProtocolSettings),
            };
            if (OperatingSystem.IsWindows() && EmbeddedRdpRequested is { } embeddedRdpRequested)
            {
                IsRdpSessionActive = true;
                await embeddedRdpRequested(session.Id, launchRequest);
                SetSessionState(session.Id, SessionState.Connected);
                SessionStatusLabel = rdpSecret is null
                    ? "RDP 已顯示在中央工作區 · 尚未指派 ID Card"
                    : "RDP 已顯示在中央工作區並使用 ID Card 自動登入";
            }
            else
            {
                await _rdpLauncher.LaunchAsync(launchRequest);
                SetSessionState(session.Id, SessionState.ExternalClientLaunched);
                SessionStatusLabel = rdpSecret is null
                    ? "已啟動平台 RDP 用戶端 · 尚未指派 ID Card"
                    : "已使用 ID Card 啟動 RDP 自動登入";
            }
        }
        catch (OperationCanceledException)
        {
            SessionStatusLabel = "RDP 連線已取消";
        }
        catch (Exception exception) when (
            exception is NotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception
                or TimeoutException or System.Net.Sockets.SocketException)
        {
            SetSessionState(session.Id, SessionState.Faulted, exception.Message);
            IsRdpSessionActive = false;
            SessionStatusLabel = exception.Message;
            await ReportMajorErrorAsync("RDP", "session-launch-failed", $"無法連線到 {connection.Endpoint.Host}:{connection.Endpoint.Port}。{exception.Message}", exception);
        }
        finally
        {
            if (rdpSecret is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(rdpSecret);
            }
            SessionPassword = string.Empty;
        }
    }

    [RelayCommand]
    private void SwitchToExistingSession()
    {
        var tab = _pendingExistingSessionId is { } id
            ? SessionTabs.FirstOrDefault(item => item.SessionId == id)
            : null;
        if (tab is not null) SelectSessionTab(tab);
        CancelSessionOpenChoice();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenAdditionalSessionAsync()
    {
        var connection = _pendingSessionConnection;
        CancelSessionOpenChoice();
        if (connection is not null) await LaunchNewConnectionAsync(connection);
    }

    [RelayCommand]
    private void CancelSessionOpenChoice()
    {
        IsSessionOpenChoiceVisible = false;
        SessionOpenChoiceMessage = string.Empty;
        _pendingSessionConnection = null;
        _pendingExistingSessionId = null;
    }

    [RelayCommand]
    private void SelectSessionTab(SessionTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        foreach (var item in SessionTabs)
        {
            item.IsSelected = ReferenceEquals(item, tab);
        }

        SelectedSessionTab = tab;
        IsViewOnly = tab.Connection.DefaultAccessMode is SessionAccessMode.ViewOnly;
        RemoteFrame = null;
        IsTerminalActive = false;
        IsWebSessionActive = false;
        IsRdpSessionActive = string.Equals(tab.ProtocolLabel, "RDP", StringComparison.OrdinalIgnoreCase);
        if (_vncSessions.TryGetValue(tab.SessionId, out var vnc))
        {
            _vncClient = vnc.Client;
            _vncFrameSink = vnc.FrameSink;
            _vncCancellation = vnc.Cancellation;
            RemoteFrame = vnc.Frame;
            OnPropertyChanged(nameof(IsVncSessionActive));
        }
        if (_sshSessions.TryGetValue(tab.SessionId, out var ssh))
        {
            _sshSession = ssh.Session;
            _sshCancellation = ssh.Cancellation;
            TerminalText = ssh.TerminalText;
            IsTerminalActive = true;
        }
        if (_localTerminalSessions.TryGetValue(tab.SessionId, out var terminal))
        {
            _localTerminal = terminal.Session;
            _localTerminalCancellation = terminal.Cancellation;
            TerminalText = terminal.TerminalText;
            IsTerminalActive = true;
        }
        if (_webSessions.TryGetValue(tab.SessionId, out var web))
        {
            WebSource = web.Source;
            WebAddress = web.Source.ToString();
            IsWebSessionActive = true;
        }
    }

    [RelayCommand]
    private async Task CloseSessionTabAsync(SessionTabViewModel? tab)
    {
        if (tab is null || !SessionTabs.Contains(tab)) return;

        var session = _sessionWorkspace.Get(tab.SessionId);
        if (session.State is SessionState.Connecting or SessionState.Connected or SessionState.ExternalClientLaunched)
        {
            SetSessionState(tab.SessionId, SessionState.Disconnecting);
        }

        if (_vncSessions.ContainsKey(tab.SessionId))
            await StopVncSessionAsync(tab.SessionId);
        if (_sshSessions.ContainsKey(tab.SessionId))
            await StopSshSessionAsync(tab.SessionId);
        if (string.Equals(session.ProtocolId, "rdp", StringComparison.OrdinalIgnoreCase) &&
            EmbeddedRdpCloseRequested is { } closeRdp)
            await closeRdp(tab.SessionId);
        if (string.Equals(session.ProtocolId, "terminal", StringComparison.OrdinalIgnoreCase))
            await StopLocalTerminalSessionAsync(tab.SessionId);
        if (session.ProtocolId is "http" or "https")
        {
            _webSessions.Remove(tab.SessionId);
            if (EmbeddedWebCloseRequested is { } closeWeb) await closeWeb(tab.SessionId);
        }

        if (_sessionWorkspace.Get(tab.SessionId).State is SessionState.Disconnecting)
            SetSessionState(tab.SessionId, SessionState.Disconnected);
        _sessionWorkspace.RemoveClosed(tab.SessionId);

        var index = SessionTabs.IndexOf(tab);
        SessionTabs.Remove(tab);
        if (ReferenceEquals(SelectedSessionTab, tab))
        {
            var next = SessionTabs.Count == 0 ? null : SessionTabs[Math.Min(index, SessionTabs.Count - 1)];
            SelectedSessionTab = null;
            if (next is not null) SelectSessionTab(next);
            else
            {
                RemoteFrame = null;
                TerminalText = string.Empty;
                IsTerminalActive = false;
                IsWebSessionActive = false;
                IsRdpSessionActive = false;
            }
        }
    }

    [RelayCommand]
    private async Task ReconnectSelectedSessionAsync()
    {
        if (SelectedSessionTab is not { } tab) return;
        var connection = tab.Connection;
        await CloseSessionTabAsync(tab);
        await LaunchNewConnectionAsync(connection);
    }

    private void UpdateSessionTab(SessionId sessionId, SessionState state)
    {
        var tab = SessionTabs.FirstOrDefault(item => item.SessionId == sessionId);
        if (tab is null)
        {
            return;
        }

        tab.StateLabel = state switch
        {
            SessionState.Connecting => "連線中",
            SessionState.Connected => "已連線",
            SessionState.ExternalClientLaunched => "外部視窗",
            SessionState.Disconnecting => "正在中斷",
            SessionState.Disconnected => "已中斷",
            SessionState.Faulted => "錯誤",
            _ => state.ToString(),
        };
    }

    private RemoteSession SetSessionState(
        SessionId sessionId,
        SessionState state,
        string? failureDetail = null)
    {
        var session = _sessionWorkspace.SetState(sessionId, state, failureDetail);
        UpdateSessionTab(sessionId, state);
        return session;
    }

    [RelayCommand]
    private async Task NavigateWebAsync()
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
            if (SelectedSessionTab is { } tab && _webSessions.TryGetValue(tab.SessionId, out var runtime))
            {
                runtime.Source = WebSource;
                if (EmbeddedWebNavigateRequested is { } navigateWeb)
                    await navigateWeb(tab.SessionId, WebSource);
            }
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

    private sealed class WebSessionRuntime(Uri source, bool viewOnly)
    {
        public Uri Source { get; set; } = source;
        public bool ViewOnly { get; } = viewOnly;
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
            await ReportMajorErrorAsync("SSH2", "credential-unavailable", SessionStatusLabel, exception);
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

        var cancellation = new CancellationTokenSource();
        var sshSession = new SshTerminalSession();
        var runtime = new SshSessionRuntime(sshSession, cancellation);
        _sshSessions.Add(sessionId, runtime);
        _sshCancellation = cancellation;
        _sshSession = sshSession;
        SshHostKeyInfo? acceptedHostKey = null;
        try
        {
            await sshSession.ConnectAsync(new SshSessionOptions
            {
                Endpoint = connection.Endpoint,
                Username = username,
                Password = password,
                ExpectedHostKeySha256 = string.IsNullOrWhiteSpace(ExpectedHostKey) ? null : ExpectedHostKey.Trim(),
                ConfirmUnknownHostKey = TrustUnknownHostKey
                    ? key => { acceptedHostKey = key; return true; }
                    : null,
                AccessMode = connection.DefaultAccessMode,
            }, cancellation.Token);
            SessionPassword = string.Empty;
            if (acceptedHostKey is not null)
            {
                ExpectedHostKey = $"SHA256:{acceptedHostKey.Sha256Fingerprint}";
                TrustUnknownHostKey = false;
            }

            if (!string.IsNullOrWhiteSpace(ExpectedHostKey))
            {
                await PersistSshHostKeyAsync(connection.Id, ExpectedHostKey);
            }

            TerminalText = string.Empty;
            IsTerminalActive = true;
            SetSessionState(sessionId, SessionState.Connected);
            SessionStatusLabel = $"SSH2 已連線 · {connection.Endpoint.Host} · {AccessModeLabel}";
            _ = ObserveSshAsync(runtime, sessionId);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or Renci.SshNet.Common.SshException
                or TimeoutException or System.Net.Sockets.SocketException)
        {
            SetSessionState(sessionId, SessionState.Faulted, exception.Message);
            SessionStatusLabel = $"SSH2 連線失敗：{exception.Message}";
            SessionPassword = string.Empty;
            var code = exception is Renci.SshNet.Common.SshAuthenticationException
                ? "authentication-failed"
                : exception is TimeoutException ? "connection-timeout" : "connection-failed";
            await ReportMajorErrorAsync("SSH2", code, $"無法連線到 {connection.Endpoint.Host}:{connection.Endpoint.Port}。{exception.Message}", exception);
            await StopSshSessionAsync(sessionId);
        }
        finally
        {
            if (vaultSecret is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(vaultSecret);
            }
        }
    }

    private async Task PersistSshHostKeyAsync(ConnectionId connectionId, string fingerprint)
    {
        var item = Connections.FirstOrDefault(candidate => candidate.Profile.Id == connectionId);
        if (item is null)
        {
            return;
        }

        var normalized = fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? $"SHA256:{fingerprint[7..].Trim()}"
            : $"SHA256:{fingerprint.Trim()}";
        var updated = item with
        {
            Profile = item.Profile with
            {
                ProtocolSettings = item.Profile.ProtocolSettings.Set(SshHostKeySetting, normalized),
            },
        };
        Connections[Connections.IndexOf(item)] = updated;
        RebuildConnectionTree(updated.Profile.Id);
        if (!IsVaultLocked)
        {
            await SaveWorkspaceAsync();
        }
        AddAuditEvent($"SSH Known Host 已保存：{updated.Profile.Endpoint.Host}");
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

    private async Task ObserveSshAsync(SshSessionRuntime runtime, SessionId sessionId)
    {
        var cancellationToken = runtime.Cancellation.Token;
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await runtime.Session.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    SetEndedSessionState(sessionId);
                    if (SelectedSessionTab?.SessionId == sessionId)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                            SessionStatusLabel = "SSH2 工作階段已由遠端結束");
                    }
                    break;
                }

                var text = runtime.Decoder.Decode(buffer.AsSpan(0, read));
                runtime.Append(text);
                if (SelectedSessionTab?.SessionId == sessionId)
                    await Dispatcher.UIThread.InvokeAsync(() => TerminalText = runtime.TerminalText);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or Renci.SshNet.Common.SshException)
        {
            SetSessionState(sessionId, SessionState.Faulted, exception.Message);
            var message = $"SSH2 工作階段中斷：{exception.Message}";
            await Dispatcher.UIThread.InvokeAsync(() => SessionStatusLabel = message);
            await ReportMajorErrorAsync("SSH2", "session-interrupted", message, exception);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
            await StopSshSessionAsync(sessionId);
        }
    }

    private async Task StopSshAsync()
    {
        foreach (var sessionId in _sshSessions.Keys.ToArray())
        {
            await StopSshSessionAsync(sessionId);
        }
    }

    private async Task StopSshSessionAsync(SessionId sessionId)
    {
        if (!_sshSessions.Remove(sessionId, out var runtime)) return;
        runtime.Cancellation.Cancel();
        await runtime.Session.DisposeAsync();
        runtime.Cancellation.Dispose();
        if (ReferenceEquals(_sshSession, runtime.Session))
        {
            _sshSession = null;
            _sshCancellation = null;
            IsTerminalActive = false;
        }
    }

    private sealed class SshSessionRuntime(SshTerminalSession session, CancellationTokenSource cancellation)
    {
        public SshTerminalSession Session { get; } = session;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TerminalOutputDecoder Decoder { get; } = new();
        public string TerminalText { get; private set; } = string.Empty;
        public void Append(string text) => TerminalText = (TerminalText + text) is { Length: > 1_000_000 } value
            ? value[^750_000..]
            : TerminalText + text;
    }

    private async Task LaunchLocalTerminalAsync(ConnectionProfile connection, SessionId sessionId)
    {
        var cancellation = new CancellationTokenSource();
        var session = new LocalTerminalSession();
        var runtime = new LocalTerminalRuntime(session, cancellation);
        _localTerminalSessions.Add(sessionId, runtime);
        _localTerminalCancellation = cancellation;
        _localTerminal = session;
        try
        {
            var savedOptions = LocalTerminalOptions.FromProtocolSettings(connection.ProtocolSettings);
            await session.StartAsync(savedOptions with
            {
                AccessMode = connection.DefaultAccessMode,
            }, cancellation.Token);
            TerminalText = string.Empty;
            IsTerminalActive = true;
            SetSessionState(sessionId, SessionState.Connected);
            SessionStatusLabel = $"本機 Terminal 已啟動 · PID {session.ProcessId} · {AccessModeLabel}";
            _ = ObserveLocalTerminalAsync(runtime, sessionId);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            SetSessionState(sessionId, SessionState.Faulted, exception.Message);
            SessionStatusLabel = $"Terminal 啟動失敗：{exception.Message}";
            await ReportMajorErrorAsync("Terminal", "launch-failed", SessionStatusLabel, exception);
            await StopLocalTerminalSessionAsync(sessionId);
        }
    }

    private async Task ObserveLocalTerminalAsync(LocalTerminalRuntime runtime, SessionId sessionId)
    {
        var cancellationToken = runtime.Cancellation.Token;
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await runtime.Session.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    SetEndedSessionState(sessionId);
                    if (SelectedSessionTab?.SessionId == sessionId)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => SessionStatusLabel = "本機 Terminal 已結束");
                    }
                    break;
                }

                runtime.Append(runtime.Decoder.Decode(buffer.AsSpan(0, read)));
                if (SelectedSessionTab?.SessionId == sessionId)
                    await Dispatcher.UIThread.InvokeAsync(() => TerminalText = runtime.TerminalText);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            SetSessionState(sessionId, SessionState.Faulted, exception.Message);
            var message = $"Terminal 中斷：{exception.Message}";
            await Dispatcher.UIThread.InvokeAsync(() => SessionStatusLabel = message);
            await ReportMajorErrorAsync("Terminal", "session-interrupted", message, exception);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
            await StopLocalTerminalSessionAsync(sessionId);
        }
    }

    private void SetEndedSessionState(SessionId sessionId)
    {
        var session = _sessionWorkspace.Get(sessionId);
        if (session.State is not SessionState.Connected) return;
        SetSessionState(sessionId, SessionState.Disconnecting);
        SetSessionState(sessionId, SessionState.Disconnected);
    }

    private async Task StopLocalTerminalAsync()
    {
        foreach (var sessionId in _localTerminalSessions.Keys.ToArray())
        {
            await StopLocalTerminalSessionAsync(sessionId);
        }
    }

    private async Task StopLocalTerminalSessionAsync(SessionId sessionId)
    {
        if (!_localTerminalSessions.Remove(sessionId, out var runtime)) return;
        runtime.Cancellation.Cancel();
        await runtime.Session.DisposeAsync();
        runtime.Cancellation.Dispose();
        if (ReferenceEquals(_localTerminal, runtime.Session))
        {
            _localTerminal = null;
            _localTerminalCancellation = null;
            IsTerminalActive = false;
        }
    }

    private sealed class LocalTerminalRuntime(LocalTerminalSession session, CancellationTokenSource cancellation)
    {
        public LocalTerminalSession Session { get; } = session;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TerminalOutputDecoder Decoder { get; } = new();
        public string TerminalText { get; private set; } = string.Empty;
        public void Append(string text)
        {
            TerminalText += text;
            if (TerminalText.Length > 1_000_000) TerminalText = TerminalText[^750_000..];
        }
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

        RefreshCompatibleVaultCredentials();
        OnPropertyChanged(nameof(SelectedCredentialName));
        OnPropertyChanged(nameof(SelectedCredentialUsername));
    }

    private void RefreshCompatibleVaultCredentials()
    {
        var selectedId = EditSelectedCredential?.Id;
        CompatibleVaultCredentials.Clear();
        foreach (var credential in VaultCredentials.Where(credential =>
                     credential.Kind is CredentialKind.UsernamePassword &&
                     string.Equals(credential.ProtocolScope, EditProtocol, StringComparison.OrdinalIgnoreCase)))
        {
            CompatibleVaultCredentials.Add(credential);
        }

        EditSelectedCredential = selectedId is { } id
            ? CompatibleVaultCredentials.FirstOrDefault(item => item.Id == id)
            : CompatibleVaultCredentials.FirstOrDefault();
    }

    private void ClearConnectionCredentialEditor()
    {
        EditCredentialName = string.Empty;
        EditCredentialUsername = string.Empty;
        EditCredentialDomain = string.Empty;
        EditCredentialPassword = string.Empty;
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
                Preferences = new WorkspacePreferences
                {
                    VaultLock = CurrentLockSettings,
                    AutomaticBackupEnabled = AutomaticBackupEnabled,
                    HideSensitiveContentFromCapture = HideSensitiveContentFromCapture,
                    ClearClipboardAfterUse = ClearClipboardAfterUse,
                    PasswordReveal = SelectedPasswordVisibility switch
                    {
                        "5 秒" => PasswordRevealMode.FiveSeconds,
                        "永久顯示" => PasswordRevealMode.Permanent,
                        _ => PasswordRevealMode.TenSeconds,
                    },
                },
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
        SelectedLockInterval = document.Preferences.VaultLock.InactivityInterval;
        LockOnSystemSleep = document.Preferences.VaultLock.LockOnSystemSleep;
        LockOnSessionLogout = document.Preferences.VaultLock.LockOnSessionLogout;
        AutomaticBackupEnabled = document.Preferences.AutomaticBackupEnabled;
        HideSensitiveContentFromCapture = document.Preferences.HideSensitiveContentFromCapture;
        ClearClipboardAfterUse = document.Preferences.ClearClipboardAfterUse;
        SelectedPasswordVisibility = document.Preferences.PasswordReveal switch
        {
            PasswordRevealMode.FiveSeconds => "5 秒",
            PasswordRevealMode.Permanent => "永久顯示",
            _ => "10 秒",
        };
        _folders.Clear();
        _folders.AddRange(document.Folders);
        RefreshFolderOptions();
        Connections.Clear();
        foreach (var connection in document.Connections)
        {
            Connections.Add(new ConnectionListItem(connection, $"{connection.ProtocolId.ToUpperInvariant()} only", connection.IsFavorite));
        }

        ConnectionTree.Clear();
        foreach (var item in BuildVisibleConnectionTree())
        {
            ConnectionTree.Add(item);
        }

        SelectedConnection = Connections.FirstOrDefault();
        SelectedTreeItem = SelectedConnection is null
            ? null
            : FindConnectionTreeItem(ConnectionTree, SelectedConnection.Profile.Id);
    }

    private void ApplyPendingConnectionImport()
    {
        if (_pendingImportedFolders.Count == 0 && _pendingImportedConnections.Count == 0)
        {
            return;
        }

        ApplyConnectionImport(_pendingImportedFolders, _pendingImportedConnections);
    }

    private void ApplyConnectionImport(
        IEnumerable<ConnectionFolder> folders,
        IEnumerable<ConnectionProfile> connections)
    {
        var knownFolderIds = _folders.Select(folder => folder.Id).ToHashSet();
        foreach (var folder in folders)
        {
            if (knownFolderIds.Add(folder.Id))
            {
                _folders.Add(folder);
            }
        }

        var knownConnectionIds = Connections.Select(item => item.Profile.Id).ToHashSet();
        foreach (var connection in connections)
        {
            if (knownConnectionIds.Add(connection.Id))
            {
                Connections.Add(new ConnectionListItem(
                    connection,
                    $"{connection.ProtocolId.ToUpperInvariant()} imported",
                    connection.IsFavorite));
            }
        }

        RefreshFolderOptions();
        ConnectionTree.Clear();
        foreach (var item in BuildVisibleConnectionTree())
        {
            ConnectionTree.Add(item);
        }

        SelectedConnection ??= Connections.FirstOrDefault();
        SelectedTreeItem = SelectedConnection is null
            ? null
            : FindConnectionTreeItem(ConnectionTree, SelectedConnection.Profile.Id);
    }

    private void RebuildConnectionTree(ConnectionId selectedId)
    {
        ConnectionTree.Clear();
        foreach (var item in BuildVisibleConnectionTree())
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
        var cancellation = new CancellationTokenSource();
        VncSessionRuntime? runtime = null;
        var frameSink = new AvaloniaRfbFrameSink(frame =>
        {
            if (runtime is null) return;
            runtime.Frame = frame;
            if (SelectedSessionTab?.SessionId == sessionId) RemoteFrame = frame;
        });
        var client = new RfbClient(new TcpRfbTransportFactory(), frameSink);
        client.ServerClipboardTextReceived += text =>
        {
            if (SelectedSessionTab?.SessionId == sessionId)
            {
                VncClipboardTextReceived?.Invoke(text);
            }
        };
        runtime = new VncSessionRuntime(client, frameSink, cancellation);
        _vncSessions.Add(sessionId, runtime);
        _vncClient = client;
        _vncFrameSink = frameSink;
        _vncCancellation = cancellation;
        try
        {
            if (ResolveCredentialDefinition(connection) is { } definition)
            {
                vaultSecret = _vault!.Reveal(definition.Id);
            }
            else if (!string.IsNullOrEmpty(SessionPassword))
            {
                vaultSecret = Encoding.UTF8.GetBytes(SessionPassword);
            }

            var server = await client.ConnectAsync(new RfbConnectionOptions
            {
                Endpoint = connection.Endpoint,
                Password = vaultSecret,
                AccessMode = connection.DefaultAccessMode,
            }, cancellation.Token);
            SetSessionState(sessionId, SessionState.Connected);
            OnPropertyChanged(nameof(IsVncSessionActive));
            var securityLabel = server.SecurityType switch
            {
                RfbSecurityType.VncAuthentication => "傳統 VNC 密碼驗證 · 傳輸未加密",
                _ => "無驗證 · 傳輸未加密",
            };
            SessionStatusLabel =
                $"VNC 已連線 · {server.Name} · {server.Width} × {server.Height} · {securityLabel}";
            _ = ObserveVncAsync(client, sessionId, cancellation.Token);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or NotSupportedException
                or TimeoutException or System.Net.Sockets.SocketException)
        {
            SetSessionState(sessionId, SessionState.Faulted, exception.Message);
            SessionStatusLabel = $"VNC 連線失敗：{exception.Message}";
            var code = exception is RfbAuthenticationException
                ? "authentication-failed"
                : exception is TimeoutException ? "connection-timeout" : "connection-failed";
            await ReportMajorErrorAsync("VNC", code, $"無法連線到 {connection.Endpoint.Host}:{connection.Endpoint.Port}。{exception.Message}", exception);
            await StopVncSessionAsync(sessionId);
        }
        finally
        {
            if (vaultSecret is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(vaultSecret);
            }
            SessionPassword = string.Empty;
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

    private CredentialDefinition? TryResolveSelectedCredential()
    {
        if (SelectedConnection is not { } selected || _vault is null || IsVaultLocked)
        {
            return null;
        }

        try
        {
            return ResolveCredentialDefinition(selected.Profile);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
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

            if (folder.GetCredential(connection.ProtocolId).Kind is CredentialReferenceKind.IdentityCard)
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
            SetSessionState(sessionId, SessionState.Faulted, exception.Message);
            var message = $"VNC 工作階段中斷：{exception.Message}";
            await Dispatcher.UIThread.InvokeAsync(() => SessionStatusLabel = message);
            await ReportMajorErrorAsync("VNC", "session-interrupted", message, exception);
        }
        finally
        {
            // A remote EOF or protocol failure must not leave a dead client in
            // the session registry. Closing a tab removes it first, so this is
            // safely a no-op for operator-requested cancellation.
            await StopVncSessionAsync(sessionId);
        }
    }

    private async Task StopVncAsync()
    {
        foreach (var sessionId in _vncSessions.Keys.ToArray())
        {
            await StopVncSessionAsync(sessionId);
        }
    }

    private async Task StopVncSessionAsync(SessionId sessionId)
    {
        if (!_vncSessions.Remove(sessionId, out var runtime)) return;
        runtime.Cancellation.Cancel();
        await runtime.Client.DisposeAsync();
        runtime.FrameSink.Dispose();
        runtime.Cancellation.Dispose();
        if (ReferenceEquals(_vncClient, runtime.Client))
        {
            _vncClient = null;
            _vncFrameSink = null;
            _vncCancellation = null;
            RemoteFrame = null;
            OnPropertyChanged(nameof(IsVncSessionActive));
        }
    }

    private sealed class VncSessionRuntime(
        RfbClient client,
        AvaloniaRfbFrameSink frameSink,
        CancellationTokenSource cancellation)
    {
        public RfbClient Client { get; } = client;
        public AvaloniaRfbFrameSink FrameSink { get; } = frameSink;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public WriteableBitmap? Frame { get; set; }
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

    private ObservableCollection<ConnectionTreeDisplayItem> BuildVisibleConnectionTree()
    {
        var complete = BuildConnectionTree(_folders, Connections);
        var query = ConnectionSearchText.Trim();
        if (query.Length == 0)
        {
            return complete;
        }

        return new ObservableCollection<ConnectionTreeDisplayItem>(complete
            .Select(item => FilterTreeItem(item, query))
            .Where(item => item is not null)
            .Select(item => item!));
    }

    private static ConnectionTreeDisplayItem? FilterTreeItem(ConnectionTreeDisplayItem item, string query)
    {
        if (item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.Connection?.Tags.Any(tag => tag.Contains(query, StringComparison.CurrentCultureIgnoreCase)) is true)
        {
            return item;
        }

        var children = item.Children
            .Select(child => FilterTreeItem(child, query))
            .Where(child => child is not null)
            .Select(child => child!)
            .ToArray();
        return item.IsFolder && children.Length > 0
            ? item with { Children = new ObservableCollection<ConnectionTreeDisplayItem>(children) }
            : null;
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

    private void RefreshFolderOptions()
    {
        FolderOptions.Clear();
        foreach (var folder in _folders.OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            FolderOptions.Add(folder);
        }
    }

    private void RefreshIdentityAssignmentTargets()
    {
        var previousConnectionId = SelectedIdentityAssignmentTarget?.ConnectionId;
        var previousFolderId = SelectedIdentityAssignmentTarget?.FolderId;
        IdentityAssignmentTargets.Clear();
        foreach (var folder in _folders.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            IdentityAssignmentTargets.Add(IdentityAssignmentTarget.ForFolder(folder));
        }

        foreach (var connection in Connections.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            IdentityAssignmentTargets.Add(IdentityAssignmentTarget.ForConnection(connection.Profile));
        }

        SelectedIdentityAssignmentTarget = IdentityAssignmentTargets.FirstOrDefault(target =>
            target.ConnectionId == previousConnectionId && target.FolderId == previousFolderId)
            ?? (SelectedConnection is { } selected
                ? IdentityAssignmentTargets.FirstOrDefault(target => target.ConnectionId == selected.Profile.Id)
                : null)
            ?? (SelectedFolder is { } selectedFolder
                ? IdentityAssignmentTargets.FirstOrDefault(target => target.FolderId == selectedFolder.Id)
                : null)
            ?? IdentityAssignmentTargets.FirstOrDefault();
    }

    private void RefreshSelectedIdentityUsages()
    {
        SelectedIdentityUsages.Clear();
        if (SelectedVaultCredential is not { } selected)
        {
            return;
        }

        foreach (var folder in _folders.Where(folder => folder.ProtocolCredentials.Values.Any(reference =>
                     reference.Kind is CredentialReferenceKind.IdentityCard &&
                     reference.VaultId == selected.VaultId &&
                     reference.CredentialId == selected.Id)))
        {
            SelectedIdentityUsages.Add($"資料夾 · {folder.Name} · {selected.ProtocolScope.ToUpperInvariant()} 預設");
        }

        var cards = VaultCredentials
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
        var resolver = new ConnectionCredentialResolver();
        foreach (var connection in Connections)
        {
            var resolved = resolver.ResolveIdentityCard(connection.Profile, _folders, cards);
            if (resolved?.CredentialId != selected.Id || resolved.VaultId != selected.VaultId)
            {
                continue;
            }

            var source = connection.Profile.Credential.Kind is CredentialReferenceKind.IdentityCard
                ? "直接指派"
                : "資料夾繼承";
            SelectedIdentityUsages.Add($"連線 · {connection.Name} · {source}");
        }

        if (SelectedIdentityUsages.Count == 0)
        {
            SelectedIdentityUsages.Add("尚未指派給任何連線或資料夾");
        }
    }

    private void AddAuditEvent(string description)
    {
        AuditEvents.Insert(0, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}　{description}");
        while (AuditEvents.Count > 100)
        {
            AuditEvents.RemoveAt(AuditEvents.Count - 1);
        }
    }

    private async Task ReportMajorErrorAsync(string category, string code, string message, Exception exception)
    {
        // Protocol exception text can contain credentials or paths. RDP hosts attach a
        // deliberately secret-free stage/HRESULT diagnostic when one is available.
        var safeDiagnostic = exception.Data["SafeDiagnostic"] as string
            ?? $"{category} operation failed ({exception.GetType().Name}).";
        await _errorLog.WriteAsync(category, code, safeDiagnostic, exception.GetType().Name);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ErrorDialogTitle = $"{category} 錯誤";
            ErrorDialogMessage = message;
            IsErrorDialogOpen = true;
        });
    }

    public async Task HandleEmbeddedRdpFailureAsync(SessionId sessionId, string reason)
    {
        try
        {
            SetSessionState(sessionId, SessionState.Faulted, reason);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        SessionStatusLabel = reason;
        await ReportMajorErrorAsync("RDP", "session-disconnected", reason, new IOException(reason));
    }

    private static ConnectionTreeDisplayItem? FindFolderTreeItem(
        IEnumerable<ConnectionTreeDisplayItem> items,
        FolderId folderId)
    {
        foreach (var item in items)
        {
            if (item.Folder?.Id == folderId)
            {
                return item;
            }

            if (FindFolderTreeItem(item.Children, folderId) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}

public sealed record IdentityAssignmentTarget(
    string DisplayName,
    ConnectionId? ConnectionId,
    FolderId? FolderId)
{
    public static IdentityAssignmentTarget ForConnection(ConnectionProfile connection) =>
        new($"連線 · {connection.Name} · {connection.ProtocolId.ToUpperInvariant()}", connection.Id, null);

    public static IdentityAssignmentTarget ForFolder(ConnectionFolder folder) =>
        new($"資料夾 · {folder.Name}", null, folder.Id);
}

public sealed record ConnectionTreeDisplayItem(
    string Name,
    string Detail,
    bool IsFolder,
    ConnectionFolder? Folder,
    ConnectionProfile? Connection,
    ObservableCollection<ConnectionTreeDisplayItem> Children)
{
    public string Glyph => IsFolder ? "▾" : "●";

    public static ConnectionTreeDisplayItem ForFolder(
        ConnectionFolder folder,
        IEnumerable<ConnectionTreeDisplayItem> children) =>
        new(folder.Name, "資料夾", true, folder, null, new(children));

    public static ConnectionTreeDisplayItem ForConnection(ConnectionProfile connection) =>
        new(
            connection.Name,
            $"{(connection.IsFavorite ? "★ · " : string.Empty)}{connection.ProtocolId.ToUpperInvariant()} · {connection.Endpoint.Host}:{connection.Endpoint.Port}",
            false,
            null,
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

    public string TagsDisplay => Profile.Tags.Count == 0 ? "無標籤" : string.Join(" · ", Profile.Tags);
}
