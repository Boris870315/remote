using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.ComponentModel;
using Remote.Desktop.ViewModels;
using Avalonia.Threading;

namespace Remote.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _isSessionFullScreen;
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
        }

        base.OnDataContextChanged(e);
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += HandleViewModelPropertyChanged;
        }

        ApplyAdaptiveLayout();
    }

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
        SessionToolbar.IsVisible = !value;
        ExitSessionFullScreenButton.IsVisible = value;

        if (value)
        {
            Grid.SetRow(ShellGrid, 0);
            Grid.SetRowSpan(ShellGrid, 2);
            ShellGrid.ColumnDefinitions = new ColumnDefinitions("0,0,*,0");
            SessionWorkspace.RowDefinitions = new RowDefinitions("0,0,*");
            return;
        }

        Grid.SetRow(ShellGrid, 1);
        Grid.SetRowSpan(ShellGrid, 1);
        SessionWorkspace.RowDefinitions = new RowDefinitions("40,44,*");
        ApplyAdaptiveLayout();
    }

    private void ApplyAdaptiveLayout()
    {
        if (_isSessionFullScreen)
        {
            return;
        }

        var isPortrait = Bounds.Height > Bounds.Width;
        var isCompact = Bounds.Width < 820 || isPortrait;
        var isMedium = !isCompact && Bounds.Width < 1120;

        var showCompactEditor = isCompact && _viewModel?.IsEditingConnection is true;
        ConnectionTree.IsVisible = !isCompact;
        Inspector.IsVisible = showCompactEditor || (!isCompact && !isMedium);
        Grid.SetColumn(Inspector, showCompactEditor ? 2 : 3);
        Inspector.HorizontalAlignment = showCompactEditor ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Stretch;
        Inspector.Width = showCompactEditor ? Math.Min(360, Math.Max(280, Bounds.Width - 52)) : double.NaN;
        NavigationRail.IsVisible = true;
        QuickConnect.IsVisible = Bounds.Width >= 760;

        ShellGrid.ColumnDefinitions = isCompact
            ? new ColumnDefinitions("52,0,*,0")
            : isMedium
                ? new ColumnDefinitions("56,220,*,0")
                : new ColumnDefinitions("56,240,*,260");
    }
}
