using System.Security.Cryptography;
using Remote.Application.Connections;
using Remote.Application.Credentials;
using Remote.Application.Vaults;

namespace Remote.Infrastructure.Import;

public sealed class MRemoteNgImportResult : IDisposable
{
    public required IReadOnlyList<ConnectionFolder> Folders { get; init; }

    public required IReadOnlyList<ConnectionProfile> Connections { get; init; }

    public required IReadOnlyList<ImportedIdentityCard> IdentityCards { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public void Dispose()
    {
        foreach (var card in IdentityCards)
        {
            card.Dispose();
        }
    }
}

public sealed class ImportedIdentityCard : IDisposable
{
    public required IdentityCard IdentityCard { get; init; }

    public required CredentialDefinition Definition { get; init; }

    public required byte[] Secret { get; init; }

    public void Dispose() => CryptographicOperations.ZeroMemory(Secret);
}
