namespace Remote.Application.Connections;

/// <summary>A hierarchical container that may provide an inherited Credential.</summary>
public sealed record ConnectionFolder
{
    public required FolderId Id { get; init; }

    public required string Name { get; init; }

    public FolderId? ParentId { get; init; }

    public ConnectionCredentialReference Credential { get; init; } =
        ConnectionCredentialReference.None;

    public IReadOnlyDictionary<string, ConnectionCredentialReference> ProtocolCredentials { get; init; } =
        new Dictionary<string, ConnectionCredentialReference>(StringComparer.OrdinalIgnoreCase);

    public ConnectionCredentialReference GetCredential(string protocolId) =>
        ProtocolCredentials.TryGetValue(protocolId, out var credential)
            ? credential
            : Credential;
}

public readonly record struct FolderId(Guid Value)
{
    public static FolderId New() => new(Guid.NewGuid());
}

public sealed record ConnectionTreeNode(
    ConnectionFolder Folder,
    IReadOnlyList<ConnectionTreeNode> Children,
    IReadOnlyList<ConnectionProfile> Connections);
