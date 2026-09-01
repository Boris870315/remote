using Remote.Application;
using Remote.Protocols;

namespace Remote.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly IRemoteSessionService _sessionService;
    private readonly IRemoteProtocol _protocol;

    public MainViewModel()
        : this(new RemoteSessionService(), new SecureShellProtocol())
    {
    }

    public MainViewModel(IRemoteSessionService sessionService, IRemoteProtocol protocol)
    {
        _sessionService = sessionService;
        _protocol = protocol;
    }

    public string Title => "Remote";

    public string Runtime => ".NET 10 · Avalonia · Windows / macOS";

    public string Protocol => $"Protocol: {_protocol.Name}";

    public string Status => _sessionService.GetStatus().State;

    public string Detail => _sessionService.GetStatus().Detail;
}
