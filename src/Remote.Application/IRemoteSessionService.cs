namespace Remote.Application;

public interface IRemoteSessionService
{
    RemoteSessionStatus GetStatus();
}

public sealed record RemoteSessionStatus(string State, string Detail);
