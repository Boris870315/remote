using System.Security.Cryptography;
using System.Text.Json;
using Remote.Application.Connections;
using Remote.Application.Credentials;
using Remote.Protocols;

namespace Remote.Infrastructure.Storage;

public sealed class EncryptedWorkspaceRepository(EncryptedWorkspaceFile workspaceFile)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public async Task SaveAsync(
        string path,
        WorkspaceDocument document,
        string masterPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(ToDto(document), JsonOptions);
        try
        {
            await workspaceFile.WriteAsync(path, plaintext, masterPassword, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<WorkspaceDocument> LoadAsync(
        string path,
        string masterPassword,
        CancellationToken cancellationToken = default)
    {
        var plaintext = await workspaceFile.ReadAsync(path, masterPassword, cancellationToken).ConfigureAwait(false);
        try
        {
            var dto = JsonSerializer.Deserialize<WorkspaceDto>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("The Workspace document is empty.");
            return FromDto(dto);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Workspace document is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static WorkspaceDto ToDto(WorkspaceDocument document) => new(
        document.SchemaVersion,
        document.Folders.Select(folder => new FolderDto(
            folder.Id.Value,
            folder.Name,
            folder.ParentId?.Value,
            ToDto(folder.Credential))).ToArray(),
        document.Connections.Select(connection => new ConnectionDto(
            connection.Id.Value,
            connection.Name,
            connection.Endpoint.ToString(),
            connection.ProtocolId,
            connection.FolderId?.Value,
            ToDto(connection.Credential),
            connection.DefaultAccessMode,
            connection.Display,
            connection.ProtocolSettings.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase))).ToArray(),
        document.IdentityCards.Select(card => new IdentityCardDto(
            card.VaultId.Value,
            card.CredentialId.Value,
            card.Name,
            card.ProtocolId,
            card.Username,
            card.Domain)).ToArray(),
        document.EncryptedPrimaryVault);

    private static WorkspaceDocument FromDto(WorkspaceDto dto)
    {
        if (dto.SchemaVersion != WorkspaceDocument.CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Workspace schema {dto.SchemaVersion} is not supported.");
        }

        var document = new WorkspaceDocument
        {
            SchemaVersion = dto.SchemaVersion,
            Folders = dto.Folders.Select(folder => new ConnectionFolder
            {
                Id = new FolderId(folder.Id),
                Name = folder.Name,
                ParentId = folder.ParentId is { } parent ? new FolderId(parent) : null,
                Credential = FromDto(folder.Credential),
            }).ToArray(),
            Connections = dto.Connections.Select(connection => new ConnectionProfile
            {
                Id = new ConnectionId(connection.Id),
                Name = connection.Name,
                Endpoint = new Uri(connection.Endpoint, UriKind.Absolute),
                ProtocolId = connection.ProtocolId,
                FolderId = connection.FolderId is { } folderId ? new FolderId(folderId) : null,
                Credential = FromDto(connection.Credential),
                DefaultAccessMode = connection.AccessMode,
                Display = connection.Display,
                ProtocolSettings = FromSettings(connection.Settings),
            }).ToArray(),
            IdentityCards = dto.IdentityCards.Select(card => new IdentityCard
            {
                VaultId = new VaultId(card.VaultId),
                CredentialId = new CredentialId(card.CredentialId),
                Name = card.Name,
                ProtocolId = card.ProtocolId,
                Username = card.Username,
                Domain = card.Domain,
            }).ToArray(),
            EncryptedPrimaryVault = dto.EncryptedPrimaryVault,
        };
        Validate(document);
        return document;
    }

    private static CredentialDto ToDto(ConnectionCredentialReference reference) =>
        new(reference.Kind, reference.VaultId?.Value, reference.CredentialId?.Value);

    private static ConnectionCredentialReference FromDto(CredentialDto dto) => dto.Kind switch
    {
        CredentialReferenceKind.None => ConnectionCredentialReference.None,
        CredentialReferenceKind.Inherited => ConnectionCredentialReference.Inherited,
        CredentialReferenceKind.IdentityCard when dto.VaultId is { } vault && dto.CredentialId is { } credential =>
            ConnectionCredentialReference.IdentityCard(new VaultId(vault), new CredentialId(credential)),
        _ => throw new InvalidDataException("The Workspace contains an invalid credential reference."),
    };

    private static ProtocolSettings FromSettings(IReadOnlyDictionary<string, string> values)
    {
        var settings = new ProtocolSettings();
        foreach (var (key, value) in values)
        {
            settings = settings.Set(key, value);
        }

        return settings;
    }

    private static void Validate(WorkspaceDocument document)
    {
        if (document.SchemaVersion != WorkspaceDocument.CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Workspace schema {document.SchemaVersion} is not supported.");
        }

        _ = new ConnectionTreeBuilder().Build(document.Folders, document.Connections);
    }

    private sealed record WorkspaceDto(
        int SchemaVersion,
        FolderDto[] Folders,
        ConnectionDto[] Connections,
        IdentityCardDto[] IdentityCards,
        byte[]? EncryptedPrimaryVault);

    private sealed record FolderDto(Guid Id, string Name, Guid? ParentId, CredentialDto Credential);

    private sealed record CredentialDto(CredentialReferenceKind Kind, Guid? VaultId, Guid? CredentialId);

    private sealed record ConnectionDto(
        Guid Id,
        string Name,
        string Endpoint,
        string ProtocolId,
        Guid? FolderId,
        CredentialDto Credential,
        SessionAccessMode AccessMode,
        DisplayPreferences Display,
        Dictionary<string, string> Settings);

    private sealed record IdentityCardDto(
        Guid VaultId,
        Guid CredentialId,
        string Name,
        string ProtocolId,
        string Username,
        string? Domain);
}
