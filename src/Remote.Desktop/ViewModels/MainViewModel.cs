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
    private readonly List<ConnectionFolder> _pendingImportedFolders = [];
    private readonly List<ConnectionProfile> _pendingImportedConnections = [];
    private ConnectionId? _editingConnectionId;
    private CredentialVault? _vault;
    private CredentialId? _editingCredentialId;
    private readonly RecoveryKeyService _recoveryKeyService = new();
    private string _activeMasterPassword = string.Empty;
    private VaultId _primaryVaultId = new(Guid.NewGuid());
    private VaultAutoLockController _autoLockController = new(new VaultLockSettings());

    public event Func<RdpExternalLaunchRequest, Task>? EmbeddedRdpRequested;

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

    public ObservableCollection<ConnectionTreeDisplayItem> ConnectionTree { get; }

    [ObservableProperty]
    private ConnectionTreeDisplayItem? selectedTreeItem;

    [ObservableProperty]
    private ConnectionFolder? selectedFolder;

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
    private bool isNotificationOpen;

    [ObservableProperty]
    private bool isSettingsPanelOpen;

    [ObservableProperty]
    private bool isAuditPanelOpen;

    [ObservableProperty]
    private bool isMonitorPanelOpen;

    [ObservableProperty]
    private string selectedMonitorOption = "螢幕 1";

    [ObservableProperty]
    private string recoveryKey = string.Empty;

    [ObservableProperty]
    private bool isDeleteConnectionConfirmationOpen;

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
        SelectedConnection?.Profile.Credential.Kind is CredentialReferenceKind.None &&
        SelectedConnection.Profile.ProtocolId is "rdp" or "vnc" or "ssh2" or "http" or "https";

    public bool ShowSessionPlaceholder => RemoteFrame is null && !IsTerminalActive && !IsWebSessionActive && !IsRdpSessionActive;

    public bool IsVncSessionActive => _vncClient?.IsConnected is true;

    public bool IsSessionConnected =>
        RemoteFrame is not null || IsTerminalActive || IsWebSessionActive || IsRdpSessionActive;

    public IReadOnlyList<string> MonitorOptions { get; } = ["螢幕 1", "螢幕 2", "全部螢幕"];

    public string IdentityEditorTitle => _editingCredentialId is null ? "新增身份卡" : "編輯身份卡";

    public string IdentitySaveLabel => _editingCredentialId is null ? "加密儲存身份卡" : "儲存身份卡變更";

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

    public async Task ShutdownAsync()
    {
        await StopVncAsync();
        await StopSshAsync();
        await StopLocalTerminalAsync();
        LockVault();
        _recoveryKeyService.Dispose();
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
        CompatibleVaultCredentials.Clear();
        EditSelectedCredential = null;
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
        byte[]? secret = NewIdentitySecret.Length == 0 ? null : Encoding.UTF8.GetBytes(NewIdentitySecret);
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
            OnPropertyChanged(nameof(IdentityEditorTitle));
            OnPropertyChanged(nameof(IdentitySaveLabel));
            OnPropertyChanged(nameof(IsEditingIdentity));
            RefreshVaultCredentials();
            await SaveWorkspaceAsync();
            VaultMessage = $"身份卡「{definition.Name}」已加密儲存";
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
        if (string.IsNullOrWhiteSpace(LegacyImportPassword))
        {
            ImportMessage = "請輸入 mRemoteNG 連線檔的加密密碼";
            return;
        }

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
    private void OpenSettingsPanel() => IsSettingsPanelOpen = true;

    [RelayCommand]
    private void CloseSettingsPanel() => IsSettingsPanelOpen = false;

    [RelayCommand]
    private void OpenAuditPanel() => IsAuditPanelOpen = true;

    [RelayCommand]
    private void CloseAuditPanel() => IsAuditPanelOpen = false;

    [RelayCommand]
    private void OpenMonitorPanel()
    {
        if (SelectedConnection is { } connection)
        {
            SelectedMonitorOption = connection.Profile.Display.MonitorSelection is MonitorSelection.All
                ? "全部螢幕"
                : $"螢幕 {(connection.Profile.Display.MonitorIndex ?? 0) + 1}";
        }
        IsMonitorPanelOpen = true;
    }

    [RelayCommand]
    private void CloseMonitorPanel() => IsMonitorPanelOpen = false;

    [RelayCommand]
    private async Task ApplyMonitorSelectionAsync()
    {
        if (SelectedConnection is not { } selected)
        {
            IsMonitorPanelOpen = false;
            return;
        }

        var all = SelectedMonitorOption == "全部螢幕";
        var monitorIndex = SelectedMonitorOption == "螢幕 2" ? 1 : 0;
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
        IsMonitorPanelOpen = false;
        if (!IsVaultLocked)
        {
            await SaveWorkspaceAsync();
        }
        SessionStatusLabel = $"顯示範圍已設定為 {SelectedMonitorOption}";
        AddAuditEvent($"變更顯示範圍：{updated.Name} · {SelectedMonitorOption}");
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
        NewIdentitySecret = string.Empty;
        OnPropertyChanged(nameof(IdentityEditorTitle));
        OnPropertyChanged(nameof(IdentitySaveLabel));
        OnPropertyChanged(nameof(IsEditingIdentity));
        VaultMessage = "密碼留空會保留原密碼；輸入新密碼才會建立新版本";
    }

    [RelayCommand]
    private void CancelIdentityCardEdit()
    {
        _editingCredentialId = null;
        NewIdentityName = string.Empty;
        NewIdentityUsername = string.Empty;
        NewIdentityDomain = string.Empty;
        NewIdentitySecret = string.Empty;
        OnPropertyChanged(nameof(IdentityEditorTitle));
        OnPropertyChanged(nameof(IdentitySaveLabel));
        OnPropertyChanged(nameof(IsEditingIdentity));
    }

    [RelayCommand]
    private void GenerateRecoveryKey()
    {
        RecoveryKey = _recoveryKeyService.Rotate();
        AddAuditEvent("已產生新的 Recovery Key");
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

    partial void OnSelectedConnectionChanged(ConnectionListItem? value)
    {
        IsViewOnly = value?.Profile.DefaultAccessMode is SessionAccessMode.ViewOnly;
        OnPropertyChanged(nameof(IsSshSelected));
        OnPropertyChanged(nameof(IsVncSelected));
        OnPropertyChanged(nameof(IsRdpSelected));
        OnPropertyChanged(nameof(SelectedUsesAllMonitors));
        OnPropertyChanged(nameof(SelectedRedirectsClipboard));
        OnPropertyChanged(nameof(SelectedRedirectsPrinters));
        OnPropertyChanged(nameof(SelectedRedirectsDrives));
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
            SelectedConnection = null;
        }
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
            ProtocolSettings = IsEditingRdp ? settings.ToProtocolSettings() : new ProtocolSettings(),
        };
        var item = new ConnectionListItem(
            profile,
            credentialReference.Kind switch
            {
                CredentialReferenceKind.Inherited => "Inherited ID Card",
                CredentialReferenceKind.IdentityCard => $"{EditProtocol.ToUpperInvariant()} ID Card",
                _ => "Prompt every time",
            },
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
            else if (connection.Credential.Kind is CredentialReferenceKind.None)
            {
                username = string.IsNullOrWhiteSpace(SessionUsername) ? null : SessionUsername.Trim();
                rdpSecret = string.IsNullOrEmpty(SessionPassword)
                    ? null
                    : Encoding.UTF8.GetBytes(SessionPassword);
            }

            var launchRequest = new RdpExternalLaunchRequest
            {
                Endpoint = connection.Endpoint,
                Username = username,
                PasswordUtf8 = rdpSecret,
                AccessMode = connection.DefaultAccessMode,
                Display = connection.Display,
                Settings = RdpConnectionSettings.FromProtocolSettings(connection.ProtocolSettings),
            };
            if (OperatingSystem.IsWindows() && EmbeddedRdpRequested is { } embeddedRdpRequested)
            {
                IsRdpSessionActive = true;
                await embeddedRdpRequested(launchRequest);
                _sessionWorkspace.SetState(session.Id, SessionState.Connected);
                SessionStatusLabel = rdpSecret is null
                    ? "RDP 已顯示在中央工作區 · 尚未指派 ID Card"
                    : "RDP 已顯示在中央工作區並使用 ID Card 自動登入";
            }
            else
            {
                await _rdpLauncher.LaunchAsync(launchRequest);
                _sessionWorkspace.SetState(session.Id, SessionState.ExternalClientLaunched);
                SessionStatusLabel = rdpSecret is null
                    ? "已啟動平台 RDP 用戶端 · 尚未指派 ID Card"
                    : "已使用 ID Card 啟動 RDP 自動登入";
            }
        }
        catch (Exception exception) when (
            exception is NotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _sessionWorkspace.SetState(session.Id, SessionState.Faulted, exception.Message);
            IsRdpSessionActive = false;
            SessionStatusLabel = exception.Message;
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
        RefreshFolderOptions();
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
                    false));
            }
        }

        RefreshFolderOptions();
        ConnectionTree.Clear();
        foreach (var item in BuildConnectionTree(_folders, Connections))
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
            else if (connection.Credential.Kind is CredentialReferenceKind.None &&
                     !string.IsNullOrEmpty(SessionPassword))
            {
                vaultSecret = Encoding.UTF8.GetBytes(SessionPassword);
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
            $"{connection.ProtocolId.ToUpperInvariant()} · {connection.Endpoint.Host}:{connection.Endpoint.Port}",
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
}
