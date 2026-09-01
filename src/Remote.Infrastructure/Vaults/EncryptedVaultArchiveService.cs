using System.Security.Cryptography;
using System.Text.Json;
using Remote.Application.Connections;
using Remote.Application.Vaults;
using Remote.Infrastructure.Security;

namespace Remote.Infrastructure.Vaults;

/// <summary>Serializes an unlocked Vault into its own authenticated encrypted envelope.</summary>
public sealed class EncryptedVaultArchiveService(WorkspaceCryptography cryptography)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public byte[] Export(CredentialVault vault, string masterPassword)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentException.ThrowIfNullOrEmpty(masterPassword);
        var entries = new List<VaultEntryDto>();
        try
        {
            foreach (var definition in vault.Credentials)
            {
                var secret = vault.Reveal(definition.Id);
                entries.Add(new VaultEntryDto(
                    definition.Id.Value,
                    definition.VaultId.Value,
                    definition.Name,
                    definition.Kind,
                    definition.ProtocolScope,
                    definition.Username,
                    definition.Domain,
                    secret));
            }

            var plaintext = JsonSerializer.SerializeToUtf8Bytes(
                new VaultArchiveDto(1, entries.ToArray()),
                JsonOptions);
            try
            {
                return cryptography.Encrypt(plaintext, masterPassword.AsSpan());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            foreach (var entry in entries)
            {
                CryptographicOperations.ZeroMemory(entry.Secret);
            }
        }
    }

    public CredentialVault Import(ReadOnlySpan<byte> encryptedArchive, string masterPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterPassword);
        var plaintext = cryptography.Decrypt(encryptedArchive, masterPassword.AsSpan());
        VaultArchiveDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<VaultArchiveDto>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("The Vault archive is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Vault archive is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        if (dto.Version != 1)
        {
            Clear(dto.Entries);
            throw new NotSupportedException($"Vault archive version {dto.Version} is not supported.");
        }

        var vault = new CredentialVault();
        try
        {
            foreach (var entry in dto.Entries)
            {
                vault.Add(new CredentialDefinition
                {
                    Id = new CredentialId(entry.Id),
                    VaultId = new VaultId(entry.VaultId),
                    Name = entry.Name,
                    Kind = entry.Kind,
                    ProtocolScope = entry.ProtocolScope,
                    Username = entry.Username,
                    Domain = entry.Domain,
                }, entry.Secret);
            }

            return vault;
        }
        catch
        {
            vault.Dispose();
            throw;
        }
        finally
        {
            Clear(dto.Entries);
        }
    }

    private static void Clear(IEnumerable<VaultEntryDto> entries)
    {
        foreach (var entry in entries)
        {
            CryptographicOperations.ZeroMemory(entry.Secret);
        }
    }

    private sealed record VaultArchiveDto(int Version, VaultEntryDto[] Entries);

    private sealed record VaultEntryDto(
        Guid Id,
        Guid VaultId,
        string Name,
        CredentialKind Kind,
        string ProtocolScope,
        string? Username,
        string? Domain,
        byte[] Secret);
}
