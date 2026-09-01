namespace Remote.Application;

public sealed class RemoteSessionService : IRemoteSessionService
{
    public RemoteSessionStatus GetStatus() =>
        new("Ready", "The cross-platform runtime is ready for a connection.");
}
