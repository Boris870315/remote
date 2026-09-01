namespace Remote.Application.Connections;

/// <summary>Storage boundary for Connections and Folders.</summary>
public interface IConnectionRepository
{
    Task<IReadOnlyList<ConnectionProfile>> GetConnectionsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectionFolder>> GetFoldersAsync(CancellationToken cancellationToken = default);

    Task SaveConnectionAsync(ConnectionProfile connection, CancellationToken cancellationToken = default);

    Task SaveFolderAsync(ConnectionFolder folder, CancellationToken cancellationToken = default);
}
