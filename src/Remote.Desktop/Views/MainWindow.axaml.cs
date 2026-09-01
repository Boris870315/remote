using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.ComponentModel;
using Remote.Desktop.ViewModels;

namespace Remote.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _isSessionFullScreen;
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyAdaptiveLayout();
        Opened += (_, _) => ApplyAdaptiveLayout();
        KeyDown += HandleWindowKeyDown;
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
