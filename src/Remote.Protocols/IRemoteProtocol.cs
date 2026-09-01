namespace Remote.Protocols;

public interface IRemoteProtocol
{
    string Name { get; }

    bool CanHandle(Uri endpoint);
}
