using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Remote.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _isSessionFullScreen;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyAdaptiveLayout();
        Opened += (_, _) => ApplyAdaptiveLayout();
        KeyDown += HandleWindowKeyDown;
    }

    private void ToggleFullScreen(object? sender, RoutedEventArgs e)
    {
        SetSessionFullScreen(!_isSessionFullScreen);
    }

    private void HandleWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape && _isSessionFullScreen)
        {
            SetSessionFullScreen(false);
            e.Handled = true;
        }
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

        ConnectionTree.IsVisible = !isCompact;
        Inspector.IsVisible = !isCompact && !isMedium;
        NavigationRail.IsVisible = true;
        QuickConnect.IsVisible = Bounds.Width >= 760;

        ShellGrid.ColumnDefinitions = isCompact
            ? new ColumnDefinitions("52,0,*,0")
            : isMedium
                ? new ColumnDefinitions("56,220,*,0")
                : new ColumnDefinitions("56,240,*,260");
    }
}
