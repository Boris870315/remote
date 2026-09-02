namespace Remote.Infrastructure.Protocols.Rdp;

public interface IRdpCredentialStore
{
    Task StoreAsync(
        Uri endpoint,
        string username,
        ReadOnlyMemory<byte> passwordUtf8,
        CancellationToken cancellationToken = default);
}

public sealed class NullRdpCredentialStore : IRdpCredentialStore
{
    public Task StoreAsync(
        Uri endpoint,
        string username,
        ReadOnlyMemory<byte> passwordUtf8,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
