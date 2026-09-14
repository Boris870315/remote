using Remote.Application.Sessions;
using Remote.Desktop.ViewModels;

namespace Remote.Application.Tests;

public sealed class RdpSessionStatusTests
{
    [Fact]
    public async Task ConnectingSurface_DoesNotReportConnectedUntilHostCompletes()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        var viewModel = new MainViewModel { QuickConnectText = "127.0.0.1:1" };
        var connected = new TaskCompletionSource();
        viewModel.EmbeddedRdpRequested += (_, _) => connected.Task;
        var statusNotifications = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSessionConnected)) statusNotifications++;
        };

        var launch = viewModel.QuickConnectCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsRdpSessionActive);
        Assert.False(viewModel.IsSessionConnected);
        Assert.Equal("連線中", Assert.Single(viewModel.SessionTabs).StateLabel);
        var beforeConnected = statusNotifications;

        connected.SetResult();
        await launch;

        Assert.True(viewModel.IsSessionConnected);
        Assert.Equal("已連線", Assert.Single(viewModel.SessionTabs).StateLabel);
        Assert.True(statusNotifications > beforeConnected);

        await viewModel.CloseSessionTabCommand.ExecuteAsync(viewModel.SelectedSessionTab);

        Assert.Empty(viewModel.SessionTabs);
        Assert.False(viewModel.IsSessionConnected);
        Assert.False(viewModel.IsRdpSessionActive);
    }

    [Fact]
    public async Task SelectedConnectingTab_DoesNotInheritBackgroundConnectedStatus()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        var viewModel = new MainViewModel { QuickConnectText = "127.0.0.1:1" };
        var pending = new Dictionary<SessionId, TaskCompletionSource>();
        viewModel.EmbeddedRdpRequested += (id, _) =>
        {
            var completion = new TaskCompletionSource();
            pending.Add(id, completion);
            return completion.Task;
        };
        viewModel.EmbeddedRdpCloseRequested += id =>
        {
            pending[id].TrySetCanceled();
            return Task.CompletedTask;
        };

        var firstLaunch = viewModel.QuickConnectCommand.ExecuteAsync(null);
        var first = Assert.Single(viewModel.SessionTabs);
        var secondLaunch = viewModel.QuickConnectCommand.ExecuteAsync(null);
        var second = viewModel.SelectedSessionTab!;

        pending[first.SessionId].SetResult();
        await firstLaunch;

        Assert.Equal("已連線", first.StateLabel);
        Assert.Equal("連線中", second.StateLabel);
        Assert.False(viewModel.IsSessionConnected);

        viewModel.SelectSessionTabCommand.Execute(first);
        Assert.True(viewModel.IsSessionConnected);
        viewModel.SelectSessionTabCommand.Execute(second);
        Assert.False(viewModel.IsSessionConnected);

        await viewModel.CloseSessionTabCommand.ExecuteAsync(second);
        await secondLaunch;
        Assert.Same(first, viewModel.SelectedSessionTab);
        Assert.True(viewModel.IsSessionConnected);

        await viewModel.CloseSessionTabCommand.ExecuteAsync(first);
    }

    [Fact]
    public async Task LateDisconnectAfterTabClose_IsIgnored()
    {
        var viewModel = new MainViewModel();

        await viewModel.HandleEmbeddedRdpFailureAsync(SessionId.New(), "late disconnect");

        Assert.False(viewModel.IsErrorDialogOpen);
        Assert.False(viewModel.IsSessionConnected);
    }

    [Fact]
    public async Task Shutdown_CancelsConnectingRdpAndRemovesItsTab()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        var viewModel = new MainViewModel { QuickConnectText = "127.0.0.1:1" };
        var connecting = new TaskCompletionSource();
        var closeRequested = false;
        viewModel.EmbeddedRdpRequested += (_, _) => connecting.Task;
        viewModel.EmbeddedRdpCloseRequested += _ =>
        {
            closeRequested = true;
            connecting.TrySetCanceled();
            return Task.CompletedTask;
        };
        var launch = viewModel.QuickConnectCommand.ExecuteAsync(null);

        await viewModel.ShutdownAsync();
        await launch;

        Assert.True(closeRequested);
        Assert.Empty(viewModel.SessionTabs);
        Assert.False(viewModel.IsRdpSessionActive);
        Assert.False(viewModel.IsSessionConnected);
    }
}
