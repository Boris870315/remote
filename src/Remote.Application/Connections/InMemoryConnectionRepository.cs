namespace Remote.Application.Connections;

/// <summary>Non-persistent repository used until the encrypted workspace store is available.</summary>
public sealed class InMemoryConnectionRepository : IConnectionRepository
{
    private readonly Dictionary<ConnectionId, ConnectionProfile> _connections = [];
    private readonly Dictionary<FolderId, ConnectionFolder> _folders = [];

    public Task<IReadOnlyList<ConnectionProfile>> GetConnectionsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConnectionProfile>>(_connections.Values.ToArray());

    public Task<IReadOnlyList<ConnectionFolder>> GetFoldersAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConnectionFolder>>(_folders.Values.ToArray());

    public Task SaveConnectionAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        _connections[connection.Id] = connection;
        return Task.CompletedTask;
    }

    public Task SaveFolderAsync(
        ConnectionFolder folder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        cancellationToken.ThrowIfCancellationRequested();
        _folders[folder.Id] = folder;
        return Task.CompletedTask;
    }
}
