using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.ComponentModel;
using Remote.Application.Layout;
using Remote.Desktop.ViewModels;
using Avalonia.Threading;
using Avalonia.Platform.Storage;

namespace Remote.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _isSessionFullScreen;
    private bool _isConnectionTreeCollapsed;
    private bool _isInspectorCollapsed;
    private byte _vncButtonMask;
    private MainViewModel? _viewModel;
    private readonly DispatcherTimer _vaultTimer;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyAdaptiveLayout();
        Opened += (_, _) => ApplyAdaptiveLayout();
        Closed += HandleClosed;
        KeyDown += HandleWindowKeyDown;
        PointerPressed += (_, _) => _viewModel?.RecordUserActivity();
        _vaultTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) =>
            _viewModel?.EvaluateVaultAutoLock());
        _vaultTimer.Start();
    }

    private async void HandleClosed(object? sender, EventArgs e)
    {
        _vaultTimer.Stop();
        if (_viewModel is not null)
        {
            await _viewModel.ShutdownAsync();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= HandleViewModelPropertyChanged;
            _viewModel.EmbeddedRdpRequested -= ConnectEmbeddedRdpAsync;
        }

        base.OnDataContextChanged(e);
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += HandleViewModelPropertyChanged;
            _viewModel.EmbeddedRdpRequested += ConnectEmbeddedRdpAsync;
        }

        ApplyAdaptiveLayout();
    }

    private Task ConnectEmbeddedRdpAsync(Remote.Infrastructure.Protocols.Rdp.RdpExternalLaunchRequest request) =>
        EmbeddedRdpSurface.ConnectAsync(request);

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsEditingConnection))
        {
            ApplyAdaptiveLayout();
        }
    }

    private void ToggleFullScreen(object? sender, RoutedEventArgs e)
    {
        SetSessionFullScreen(!_isSessionFullScreen);
    }

    private void GoWebBack(object? sender, RoutedEventArgs e)
    {
        if (WebSessionView.CanGoBack)
        {
            WebSessionView.GoBack();
        }
    }

    private void RefreshWeb(object? sender, RoutedEventArgs e) => WebSessionView.Refresh();

    private async void RefreshSession(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.IsWebSessionActive is true)
        {
            WebSessionView.Refresh();
            return;
        }

        if (_viewModel is not null)
        {
            await _viewModel.OpenSelectedSessionCommand.ExecuteAsync(null);
        }
    }

    private async void ApplyMonitorSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && _viewModel.ApplyMonitorSelectionCommand.CanExecute(null))
        {
            await _viewModel.ApplyMonitorSelectionCommand.ExecuteAsync(null);
        }
    }

    private void FitSessionToWindow(object? sender, RoutedEventArgs e) =>
        RemoteSurface.Stretch = Avalonia.Media.Stretch.Uniform;

    private void ToggleConnectionTree(object? sender, RoutedEventArgs e)
    {
        _isConnectionTreeCollapsed = !_isConnectionTreeCollapsed;
        ApplyAdaptiveLayout();
    }

    private void HandleConnectionTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Control source ||
            !e.GetCurrentPoint(ConnectionTreeView).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // The built-in expander already owns clicks on its arrow. Handle the rest
        // of the folder row so its icon, label, detail and empty area all toggle it.
        if (source is ToggleButton || source.GetVisualAncestors().Any(ancestor => ancestor is ToggleButton))
        {
            return;
        }

        var treeItem = source as TreeViewItem
            ?? source.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        if (treeItem?.DataContext is ConnectionTreeDisplayItem { IsFolder: true })
        {
            treeItem.IsExpanded = !treeItem.IsExpanded;
        }
    }

    private void ToggleInspector(object? sender, RoutedEventArgs e)
    {
        _isInspectorCollapsed = !_isInspectorCollapsed;
        ApplyAdaptiveLayout();
    }

    private async void ImportMRemoteNg(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "選擇 mRemoteNG 連線檔",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("mRemoteNG Connections") { Patterns = ["*.xml", "*.confCons"] },
            ],
        });
        var file = files.FirstOrDefault();
        if (file is not null && file.TryGetLocalPath() is { } path)
        {
            await _viewModel.ImportMRemoteNgAsync(path);
        }
    }

    private async void RestoreWorkspaceBackup(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "選擇 Remote 加密備份",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Remote Workspace Backup") { Patterns = ["*.rmtw.backup"] },
            ],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            await _viewModel.RestoreBackupAsync(path);
        }
    }

    private void HandleWindowKeyDown(object? sender, KeyEventArgs e)
    {
        _viewModel?.RecordUserActivity();
        if (e.Key is Key.Escape && _isSessionFullScreen)
        {
            SetSessionFullScreen(false);
            e.Handled = true;
        }
    }

    private async void HandleRemotePointerPressed(object? sender, PointerPressedEventArgs e) =>
        await SendRemotePointerAsync(e, focus: true);

    private async void HandleRemotePointerMoved(object? sender, PointerEventArgs e) =>
        await SendRemotePointerAsync(e, focus: false);

    private async void HandleRemotePointerReleased(object? sender, PointerReleasedEventArgs e) =>
        await SendRemotePointerAsync(e, focus: false);

    private async Task SendRemotePointerAsync(PointerEventArgs e, bool focus)
    {
        if (_viewModel?.IsVncSessionActive is not true || _viewModel.RemoteFrame is null)
        {
            return;
        }

        if (focus)
        {
            RemoteSurface.Focus();
        }

        var point = e.GetCurrentPoint(RemoteSurface);
        _vncButtonMask = point.Properties.IsLeftButtonPressed ? (byte)1
            : point.Properties.IsMiddleButtonPressed ? (byte)2
            : point.Properties.IsRightButtonPressed ? (byte)4
            : (byte)0;
        if (!TryMapRemotePoint(point.Position, out var x, out var y))
        {
            return;
        }

        e.Handled = await _viewModel.SendVncPointerAsync(_vncButtonMask, x, y);
    }

    private async void HandleRemoteKeyDown(object? sender, KeyEventArgs e) =>
        await SendRemoteKeyAsync(e, true);

    private async void HandleRemoteKeyUp(object? sender, KeyEventArgs e) =>
        await SendRemoteKeyAsync(e, false);

    private async Task SendRemoteKeyAsync(KeyEventArgs e, bool isDown)
    {
        var keySym = ToRfbKeySym(e.Key);
        if (keySym is not null && _viewModel is not null)
        {
            e.Handled = await _viewModel.SendVncKeyAsync(keySym.Value, isDown);
        }
    }

    private bool TryMapRemotePoint(Avalonia.Point point, out ushort x, out ushort y)
    {
        x = y = 0;
        var frame = _viewModel?.RemoteFrame;
        if (frame is null || RemoteSurface.Bounds.Width <= 0 || RemoteSurface.Bounds.Height <= 0)
        {
            return false;
        }

        var scale = Math.Min(
            RemoteSurface.Bounds.Width / frame.PixelSize.Width,
            RemoteSurface.Bounds.Height / frame.PixelSize.Height);
        var displayedWidth = frame.PixelSize.Width * scale;
        var displayedHeight = frame.PixelSize.Height * scale;
        var remoteX = (point.X - ((RemoteSurface.Bounds.Width - displayedWidth) / 2)) / scale;
        var remoteY = (point.Y - ((RemoteSurface.Bounds.Height - displayedHeight) / 2)) / scale;
        if (remoteX < 0 || remoteY < 0 || remoteX >= frame.PixelSize.Width || remoteY >= frame.PixelSize.Height)
        {
            return false;
        }

        x = (ushort)remoteX;
        y = (ushort)remoteY;
        return true;
    }

    private static uint? ToRfbKeySym(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return (uint)('a' + (key - Key.A));
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return (uint)('0' + (key - Key.D0));
        }

        return key switch
        {
            Key.Enter => 0xFF0D,
            Key.Back => 0xFF08,
            Key.Tab => 0xFF09,
            Key.Escape => 0xFF1B,
            Key.Delete => 0xFFFF,
            Key.Left => 0xFF51,
            Key.Up => 0xFF52,
            Key.Right => 0xFF53,
            Key.Down => 0xFF54,
            Key.LeftShift or Key.RightShift => 0xFFE1,
            Key.LeftCtrl or Key.RightCtrl => 0xFFE3,
            Key.LWin or Key.RWin => 0xFFEB,
            Key.LeftAlt or Key.RightAlt => 0xFFE9,
            Key.Space => 0x20,
            _ => null,
        };
    }

    private void SetSessionFullScreen(bool value)
    {
        _isSessionFullScreen = value;
        ApplicationHeader.IsVisible = !value;
        NavigationRail.IsVisible = !value;
        ConnectionTree.IsVisible = !value;
        Inspector.IsVisible = !value;
        SessionTabs.IsVisible = !value;
        ExitSessionFullScreenButton.IsVisible = value;

        if (value)
        {
            Grid.SetRow(ShellGrid, 0);
            Grid.SetRowSpan(ShellGrid, 2);
            ShellGrid.ColumnDefinitions = new ColumnDefinitions("0,0,*,0");
            SessionWorkspace.RowDefinitions = new RowDefinitions("0,*");
            return;
        }

        Grid.SetRow(ShellGrid, 1);
        Grid.SetRowSpan(ShellGrid, 1);
        SessionWorkspace.RowDefinitions = new RowDefinitions("40,*");
        ApplyAdaptiveLayout();
    }

    private void ApplyAdaptiveLayout()
    {
        if (_isSessionFullScreen)
        {
            return;
        }

        var state = AdaptiveShellLayout.Calculate(
            Bounds.Width,
            Bounds.Height,
            _viewModel?.IsEditingConnection is true);
        var showCompactEditor = state.Mode is AdaptiveShellMode.Compact &&
            _viewModel?.IsEditingConnection is true;
        var showConnectionTree = state.ShowConnectionTree && !_isConnectionTreeCollapsed;
        var showInspector = state.ShowInspector && !_isInspectorCollapsed;
        ConnectionTree.IsVisible = showConnectionTree;
        Inspector.IsVisible = showInspector;
        Grid.SetColumn(Inspector, showCompactEditor ? 2 : 3);
        Inspector.HorizontalAlignment = showCompactEditor ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Stretch;
        Inspector.Width = showCompactEditor ? state.CompactEditorWidth : double.NaN;
        NavigationRail.IsVisible = true;
        QuickConnect.IsVisible = state.ShowQuickConnect;

        ShellGrid.ColumnDefinitions = state.Mode switch
        {
            AdaptiveShellMode.Compact => new ColumnDefinitions("52,0,*,0"),
            AdaptiveShellMode.Medium => new ColumnDefinitions(showConnectionTree ? "56,220,*,0" : "56,0,*,0"),
            _ => new ColumnDefinitions($"56,{(showConnectionTree ? 240 : 0)},*,{(showInspector ? 260 : 0)}"),
        };

        LeftPanelHandle.IsVisible = state.ShowConnectionTree;
        LeftPanelHandle.Content = showConnectionTree ? "‹" : "›";
        RightPanelHandle.IsVisible = state.ShowInspector;
        RightPanelHandle.Content = showInspector ? "›" : "‹";
    }
}
