using System.Net.Sockets;

namespace Remote.Infrastructure.Protocols.Vnc;

public interface IRfbTransportFactory
{
    Task<RfbTransport> ConnectAsync(string host, int port, CancellationToken cancellationToken);
}

public sealed class TcpRfbTransportFactory : IRfbTransportFactory
{
    public async Task<RfbTransport> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new(client.GetStream(), client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

public sealed class RfbTransport(Stream stream, IDisposable owner) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;

    public ValueTask DisposeAsync()
    {
        Stream.Dispose();
        owner.Dispose();
        return ValueTask.CompletedTask;
    }
}
