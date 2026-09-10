using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.ComponentModel;
using Remote.Application.Layout;
using Remote.Desktop.ViewModels;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Remote.Application.Sessions;
using Remote.Desktop.Protocols.Rdp;
using Remote.Desktop.Protocols.Vnc;

namespace Remote.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _isSessionFullScreen;
    private bool _shutdownCompleted;
    private bool _shutdownInProgress;
    private bool _isConnectionTreeCollapsed;
    private bool _isInspectorCollapsed;
    private byte _vncButtonMask;
    private MainViewModel? _viewModel;
    private readonly Dictionary<SessionId, AvaloniaEmbeddedRdpHost> _rdpHosts = [];
    private readonly Dictionary<SessionId, MacOsRdpRuntime> _macRdpSessions = [];
    private readonly Dictionary<SessionId, NativeWebView> _webViews = [];
    private readonly DispatcherTimer _vaultTimer;
    private readonly DispatcherTimer _terminalResizeTimer;

    public MainWindow()
    {
        InitializeComponent();
        SessionWorkspace.SizeChanged += (_, _) => ScheduleTerminalResize();
        _terminalResizeTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            DispatcherPriority.Background,
            (sender, _) =>
            {
                (sender as DispatcherTimer)?.Stop();
                _viewModel?.ResizeSelectedTerminal(
                    SessionWorkspace.Bounds.Width,
                    SessionWorkspace.Bounds.Height);
                ResizeSelectedMacRdp();
            });
        SizeChanged += (_, _) =>
        {
            ApplyAdaptiveLayout();
            ScheduleTerminalResize();
        };
        Opened += (_, _) =>
        {
            RefreshMonitorOptions();
            ApplyAdaptiveLayout();
        };
        Closing += HandleClosing;
        Closed += HandleClosed;
        KeyDown += HandleWindowKeyDown;
        PointerPressed += (_, _) => _viewModel?.RecordUserActivity();
        AddHandler(PointerPressedEvent, HandleSecretRevealPressed, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, HandleSecretRevealReleased, RoutingStrategies.Tunnel, true);
        _vaultTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) =>
            _viewModel?.EvaluateVaultAutoLock());
        _vaultTimer.Start();
    }

    private void HandleSecretRevealPressed(object? sender, PointerPressedEventArgs e)
    {
        if (FindRevealButton(e.Source) is null) return;
        _viewModel?.BeginSecretReveal();
        e.Handled = true;
    }

    private void HandleSecretRevealReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_viewModel?.AreEditableSecretsVisible is not true) return;
        _viewModel.EndSecretReveal();
        e.Handled = true;
    }

    private static Button? FindRevealButton(object? source)
    {
        var control = source as Control;
        var button = control as Button ?? control?.GetVisualAncestors().OfType<Button>().FirstOrDefault();
        return string.Equals(button?.Content?.ToString(), "顯示", StringComparison.Ordinal) ? button : null;
    }

    private async void HandleClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownCompleted) return;
        e.Cancel = true;
        if (_shutdownInProgress) return;
        _shutdownInProgress = true;
        try
        {
            if (_viewModel is not null) await _viewModel.ShutdownAsync();
        }
        finally
        {
            _shutdownCompleted = true;
            Close();
        }
    }

    private void HandleClosed(object? sender, EventArgs e)
    {
        _vaultTimer.Stop();
        _terminalResizeTimer.Stop();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= HandleViewModelPropertyChanged;
            _viewModel.EmbeddedRdpRequested -= ConnectEmbeddedRdpAsync;
            _viewModel.EmbeddedRdpCloseRequested -= CloseEmbeddedRdpAsync;
            _viewModel.EmbeddedWebRequested -= OpenEmbeddedWebAsync;
            _viewModel.EmbeddedWebNavigateRequested -= NavigateEmbeddedWebAsync;
            _viewModel.EmbeddedWebCloseRequested -= CloseEmbeddedWebAsync;
            _viewModel.VncClipboardTextReceived -= HandleVncClipboardTextReceived;
            _viewModel.SessionViewOnlyChanged -= HandleSessionViewOnlyChanged;
        }

        base.OnDataContextChanged(e);
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += HandleViewModelPropertyChanged;
            _viewModel.EmbeddedRdpRequested += ConnectEmbeddedRdpAsync;
            _viewModel.EmbeddedRdpCloseRequested += CloseEmbeddedRdpAsync;
            _viewModel.EmbeddedWebRequested += OpenEmbeddedWebAsync;
            _viewModel.EmbeddedWebNavigateRequested += NavigateEmbeddedWebAsync;
            _viewModel.EmbeddedWebCloseRequested += CloseEmbeddedWebAsync;
            _viewModel.VncClipboardTextReceived += HandleVncClipboardTextReceived;
            _viewModel.SessionViewOnlyChanged += HandleSessionViewOnlyChanged;
        }

        ApplyAdaptiveLayout();
    }

    private void HandleSessionViewOnlyChanged(SessionId sessionId, bool viewOnly)
    {
        if (_rdpHosts.TryGetValue(sessionId, out var windowsHost))
            windowsHost.SetViewOnly(viewOnly);
        if (!_macRdpSessions.TryGetValue(sessionId, out var runtime)) return;
        runtime.Session.SetViewOnly(viewOnly);
        if (!viewOnly) return;
        runtime.ControlDown = runtime.AltDown = runtime.ShiftDown = false;
        runtime.SuppressPasteKeyUp = false;
    }

    private async Task ConnectEmbeddedRdpAsync(
        SessionId sessionId,
        Remote.Infrastructure.Protocols.Rdp.RdpExternalLaunchRequest request)
    {
        if (OperatingSystem.IsMacOS())
        {
            await ConnectMacOsEmbeddedRdpAsync(sessionId, request);
            return;
        }

        if (!_rdpHosts.TryGetValue(sessionId, out var host))
        {
            host = new AvaloniaEmbeddedRdpHost();
            host.UnexpectedlyDisconnected += reason =>
                _ = _viewModel?.HandleEmbeddedRdpFailureAsync(sessionId, reason);
            _rdpHosts.Add(sessionId, host);
            EmbeddedRdpSurfaces.Children.Add(host);
        }

        ShowSelectedRdpHost();
        try
        {
            await host.ConnectAsync(request);
            ShowSelectedRdpHost();
        }
        catch
        {
            if (_rdpHosts.Remove(sessionId, out var failedHost))
            {
                await failedHost.DisconnectAsync();
                EmbeddedRdpSurfaces.Children.Remove(failedHost);
            }
            throw;
        }
    }

    private async Task CloseEmbeddedRdpAsync(SessionId sessionId)
    {
        if (_macRdpSessions.Remove(sessionId, out var macRuntime))
        {
            await macRuntime.Session.DisposeAsync();
            macRuntime.FrameSink.Dispose();
            return;
        }
        if (!_rdpHosts.Remove(sessionId, out var host)) return;
        await host.DisconnectAsync();
        EmbeddedRdpSurfaces.Children.Remove(host);
    }

    private Task OpenEmbeddedWebAsync(SessionId sessionId, Uri source, bool viewOnly)
    {
        var webView = new NativeWebView { Source = source, IsHitTestVisible = !viewOnly };
        _webViews.Add(sessionId, webView);
        WebSessionSurfaces.Children.Add(webView);
        ShowSelectedWebView();
        return Task.CompletedTask;
    }

    private Task NavigateEmbeddedWebAsync(SessionId sessionId, Uri source)
    {
        if (_webViews.TryGetValue(sessionId, out var webView)) webView.Source = source;
        return Task.CompletedTask;
    }

    private Task CloseEmbeddedWebAsync(SessionId sessionId)
    {
        if (_webViews.Remove(sessionId, out var webView)) WebSessionSurfaces.Children.Remove(webView);
        return Task.CompletedTask;
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsEditingConnection))
        {
            ApplyAdaptiveLayout();
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedSessionTab))
        {
            ShowSelectedRdpHost();
            ShowSelectedWebView();
            ApplyRemoteSurfaceScale();
            ScheduleTerminalResize();
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedConnection))
        {
            ApplyRemoteSurfaceScale();
        }
    }

    private void ScheduleTerminalResize()
    {
        _terminalResizeTimer.Stop();
        _terminalResizeTimer.Start();
    }

    private void ResizeSelectedMacRdp()
    {
        if (!TryGetSelectedMacRdp(out var runtime)) return;
        var width = Math.Clamp((int)SessionWorkspace.Bounds.Width, 640, 8192);
        var height = Math.Clamp((int)SessionWorkspace.Bounds.Height, 480, 8192);
        if (runtime.Width == width && runtime.Height == height) return;
        if (runtime.Session.Resize(width, height))
        {
            runtime.Width = width;
            runtime.Height = height;
        }
    }

    private void ShowSelectedWebView()
    {
        var selected = _viewModel?.SelectedSessionTab?.SessionId;
        foreach (var pair in _webViews) pair.Value.IsVisible = pair.Key == selected;
    }

    private void ShowSelectedRdpHost()
    {
        var selected = _viewModel?.SelectedSessionTab?.SessionId;
        foreach (var pair in _rdpHosts)
        {
            pair.Value.SetSessionVisible(pair.Key == selected);
        }
        if (selected is { } sessionId && _macRdpSessions.TryGetValue(sessionId, out var runtime))
            _viewModel?.SetEmbeddedRdpFrame(sessionId, runtime.FrameSink.Frame);
    }

    private async Task ConnectMacOsEmbeddedRdpAsync(
        SessionId sessionId,
        Remote.Infrastructure.Protocols.Rdp.RdpExternalLaunchRequest request)
    {
        var session = new MacOsFreeRdpSession();
        var sink = new AvaloniaFreeRdpFrameSink(frame =>
        {
            _viewModel?.SetEmbeddedRdpFrame(sessionId, frame);
            // WriteableBitmap keeps the same object identity while its pixels change.
            // Avalonia therefore needs an explicit visual invalidation; otherwise the
            // latest RDP frame may remain hidden until a layout resize repaints it.
            if (_viewModel?.SelectedSessionTab?.SessionId == sessionId)
                RemoteSurface.InvalidateVisual();
        });
        var runtime = new MacOsRdpRuntime(session, sink);
        session.FrameReceived += sink.Publish;
        session.StateChanged += (state, errorCode, message) =>
        {
            if (state == 2 && _macRdpSessions.ContainsKey(sessionId))
                Dispatcher.UIThread.Post(() =>
                    _ = _viewModel?.HandleEmbeddedRdpFailureAsync(sessionId,
                        MacOsFreeRdpSession.DescribeError(errorCode, message)));
        };
        _macRdpSessions.Add(sessionId, runtime);
        try
        {
            await session.ConnectAsync(request,
                Math.Max(640, (int)SessionWorkspace.Bounds.Width),
                Math.Max(480, (int)SessionWorkspace.Bounds.Height));
        }
        catch
        {
            _macRdpSessions.Remove(sessionId);
            await session.DisposeAsync();
            sink.Dispose();
            throw;
        }
    }

    private sealed class MacOsRdpRuntime(MacOsFreeRdpSession session, AvaloniaFreeRdpFrameSink frameSink)
    {
        public MacOsFreeRdpSession Session { get; } = session;
        public AvaloniaFreeRdpFrameSink FrameSink { get; } = frameSink;
        public bool ControlDown { get; set; }
        public bool AltDown { get; set; }
        public bool ShiftDown { get; set; }
        public bool SuppressPasteKeyUp { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    private void RefreshMonitorOptions()
    {
        if (_viewModel is null) return;
        var screens = Screens.All
            .OrderByDescending(screen => screen.IsPrimary)
            .ToArray();
        _viewModel.SetAvailableMonitorCount(screens.Length);
    }

    private void ToggleFullScreen(object? sender, RoutedEventArgs e)
    {
        SetSessionFullScreen(!_isSessionFullScreen);
    }

    private void GoWebBack(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedWebView() is { CanGoBack: true } webView)
        {
            webView.GoBack();
        }
    }

    private void RefreshWeb(object? sender, RoutedEventArgs e) => GetSelectedWebView()?.Refresh();

    private async void RefreshSession(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.IsWebSessionActive is true)
        {
            GetSelectedWebView()?.Refresh();
            return;
        }

        if (_viewModel is not null)
        {
            await _viewModel.ReconnectSelectedSessionCommand.ExecuteAsync(null);
        }
    }

    private NativeWebView? GetSelectedWebView() =>
        _viewModel?.SelectedSessionTab is { } tab && _webViews.TryGetValue(tab.SessionId, out var webView)
            ? webView
            : null;

    private async void ApplyMonitorSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && _viewModel.ApplyMonitorSelectionCommand.CanExecute(null))
        {
            await _viewModel.ApplyMonitorSelectionCommand.ExecuteAsync(null);
        }
    }

    private async void ApplyDisplayScale(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (_viewModel.ApplyDisplayScaleCommand.CanExecute(null))
        {
            await _viewModel.ApplyDisplayScaleCommand.ExecuteAsync(null);
        }
        ApplyRemoteSurfaceScale();
    }

    private async void UpdateInspectorRdpSettings(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.IsRdpSelected)
        {
            return;
        }

        await _viewModel.UpdateSelectedRdpSettingsAsync(
            _viewModel.SelectedUsesAllMonitors,
            InspectorRedirectClipboard.IsChecked is true,
            InspectorRedirectPrinters.IsChecked is true,
            InspectorRedirectDrives.IsChecked is true,
            InspectorRedirectMicrophone.IsChecked is true,
            InspectorRedirectCamera.IsChecked is true);
    }

    private void ApplyRemoteSurfaceScale()
    {
        var mode = _viewModel?.SelectedConnection?.Profile.Display.ScaleMode ??
            Remote.Application.Connections.DisplayScaleMode.Fit;
        var scroll = mode is Remote.Application.Connections.DisplayScaleMode.Scroll;
        RemoteSurfaceScroller.HorizontalScrollBarVisibility = scroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        RemoteSurfaceScroller.VerticalScrollBarVisibility = scroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        RemoteSurface.Stretch = mode switch
        {
            Remote.Application.Connections.DisplayScaleMode.Fill => Avalonia.Media.Stretch.UniformToFill,
            Remote.Application.Connections.DisplayScaleMode.ActualSize or
                Remote.Application.Connections.DisplayScaleMode.Scroll => Avalonia.Media.Stretch.None,
            _ => Avalonia.Media.Stretch.Uniform,
        };
        RemoteSurface.HorizontalAlignment = scroll ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Stretch;
        RemoteSurface.VerticalAlignment = scroll ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Stretch;
    }

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

    private async void HandleConnectionTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null || e.Source is not Control source)
        {
            return;
        }

        var treeItem = source as TreeViewItem
            ?? source.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        if (treeItem?.DataContext is not ConnectionTreeDisplayItem { Connection: { } connection })
        {
            return;
        }

        e.Handled = true;
        await _viewModel.ActivateConnectionFromTreeAsync(connection);
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

    private async void HandleRemotePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_viewModel?.IsViewOnly is true) { e.Handled = true; return; }
        if (!TryMapRemotePoint(e.GetPosition(RemoteSurface), out var x, out var y))
        {
            return;
        }

        if (TryGetSelectedMacRdp(out var macRdp))
        {
            var delta = (short)Math.Clamp((int)(e.Delta.Y * 120), short.MinValue, short.MaxValue);
            e.Handled = delta != 0 && macRdp.Session.SendWheel(x, y, delta);
            return;
        }
        if (_viewModel?.IsVncSessionActive is not true) return;

        var wheelMask = e.Delta.Y > 0 ? (byte)8 : e.Delta.Y < 0 ? (byte)16 : (byte)0;
        if (wheelMask == 0) return;
        var pressed = await _viewModel.SendVncPointerAsync(wheelMask, x, y);
        var released = await _viewModel.SendVncPointerAsync(0, x, y);
        e.Handled = pressed && released;
    }

    private async Task SendRemotePointerAsync(PointerEventArgs e, bool focus)
    {
        if (_viewModel?.IsViewOnly is true) { e.Handled = true; return; }
        if (_viewModel?.RemoteFrame is null)
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

        if (TryGetSelectedMacRdp(out var macRdp))
            e.Handled = macRdp.Session.SendMouse(x, y, _vncButtonMask);
        else if (_viewModel.IsVncSessionActive)
            e.Handled = await _viewModel.SendVncPointerAsync(_vncButtonMask, x, y);
    }

    private async void HandleRemoteKeyDown(object? sender, KeyEventArgs e) =>
        await SendRemoteKeyDownAsync(e);

    private async void HandleRemoteKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.V && TryGetSelectedMacRdp(out var macRdp) &&
            macRdp.SuppressPasteKeyUp)
        {
            macRdp.SuppressPasteKeyUp = false;
            e.Handled = true;
            return;
        }
        await SendRemoteKeyAsync(e, false);
    }

    private async Task SendRemoteKeyDownAsync(KeyEventArgs e)
    {
        if (_viewModel?.IsViewOnly is true) { e.Handled = true; return; }
        if (e.Key is Key.V && TryGetSelectedMacRdp(out var macRdp) &&
            _viewModel?.SelectedRedirectsClipboard is true &&
            (e.KeyModifiers.HasFlag(KeyModifiers.Meta) ||
             e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var text = clipboard is null ? null : await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                // Release a Command/Control modifier that may already have been
                // forwarded; Unicode input must not be interpreted as shortcuts.
                SynchronizeMacRdpModifiers(macRdp, false, false, false);
                macRdp.SuppressPasteKeyUp = true;
                e.Handled = macRdp.Session.SendUnicodeText(text);
                return;
            }
        }

        if (e.Key is Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            _viewModel?.IsVncSessionActive is true)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var text = clipboard is null ? null : await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                await _viewModel.SendVncClipboardTextAsync(text);
            }
        }

        await SendRemoteKeyAsync(e, true);
    }

    private void HandleVncClipboardTextReceived(string text)
    {
        if (_viewModel?.IsViewOnly is true) return;
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        });
    }

    private async Task SendRemoteKeyAsync(KeyEventArgs e, bool isDown)
    {
        if (_viewModel?.IsViewOnly is true) { e.Handled = true; return; }
        if (TryGetSelectedMacRdp(out var macRdp))
        {
            if (OperatingSystem.IsMacOS())
            {
                // macOS does not consistently raise a separate Command/Control
                // key event for a focused Avalonia Image. Derive and synchronize
                // the Windows modifier state from every key event instead, so a
                // Command+C event always reaches RDP as Ctrl-down, C-down.
                var isControlKey = e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LWin or Key.RWin;
                var isAltKey = e.Key is Key.LeftAlt or Key.RightAlt;
                var isShiftKey = e.Key is Key.LeftShift or Key.RightShift;
                var wantsControl = isControlKey
                    ? isDown
                    : e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                      e.KeyModifiers.HasFlag(KeyModifiers.Meta);
                var wantsAlt = isAltKey ? isDown : e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                var wantsShift = isShiftKey ? isDown : e.KeyModifiers.HasFlag(KeyModifiers.Shift);

                var modifiersSent = SynchronizeMacRdpModifiers(
                    macRdp, wantsControl, wantsAlt, wantsShift);
                if (isControlKey || isAltKey || isShiftKey)
                {
                    e.Handled = modifiersSent;
                    return;
                }
            }

            var virtualKey = RdpVirtualKeyMapper.Map(e.Key);
            if (virtualKey is not null) e.Handled = macRdp.Session.SendKey(virtualKey.Value, isDown);
            return;
        }
        var keySym = RfbKeySymMapper.Map(e.Key);
        if (keySym is not null && _viewModel is not null)
        {
            e.Handled = await _viewModel.SendVncKeyAsync(keySym.Value, isDown);
        }
    }

    private static bool SynchronizeMacRdpModifiers(
        MacOsRdpRuntime runtime, bool control, bool alt, bool shift)
    {
        var sent = true;
        if (runtime.ControlDown != control)
        {
            sent &= runtime.Session.SendKey(0xA2, control); // VK_LCONTROL
            runtime.ControlDown = control;
        }
        if (runtime.AltDown != alt)
        {
            sent &= runtime.Session.SendKey(0xA4, alt); // VK_LMENU
            runtime.AltDown = alt;
        }
        if (runtime.ShiftDown != shift)
        {
            sent &= runtime.Session.SendKey(0xA0, shift); // VK_LSHIFT
            runtime.ShiftDown = shift;
        }
        return sent;
    }

    private bool TryGetSelectedMacRdp(out MacOsRdpRuntime runtime)
    {
        if (_viewModel?.SelectedSessionTab is { } tab &&
            _macRdpSessions.TryGetValue(tab.SessionId, out var selected))
        {
            runtime = selected;
            return true;
        }
        runtime = null!;
        return false;
    }

    private bool TryMapRemotePoint(Avalonia.Point point, out ushort x, out ushort y)
    {
        x = y = 0;
        var frame = _viewModel?.RemoteFrame;
        if (frame is null || RemoteSurface.Bounds.Width <= 0 || RemoteSurface.Bounds.Height <= 0)
        {
            return false;
        }

        var mode = _viewModel?.SelectedConnection?.Profile.Display.ScaleMode ??
            Remote.Application.Connections.DisplayScaleMode.Fit;
        var widthScale = RemoteSurface.Bounds.Width / frame.PixelSize.Width;
        var heightScale = RemoteSurface.Bounds.Height / frame.PixelSize.Height;
        var scale = mode switch
        {
            Remote.Application.Connections.DisplayScaleMode.Fill => Math.Max(widthScale, heightScale),
            Remote.Application.Connections.DisplayScaleMode.ActualSize or
                Remote.Application.Connections.DisplayScaleMode.Scroll => 1,
            _ => Math.Min(widthScale, heightScale),
        };
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
