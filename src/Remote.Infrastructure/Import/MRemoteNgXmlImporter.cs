using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Remote.Application.Connections;
using Remote.Application.Credentials;
using Remote.Application.Vaults;

namespace Remote.Infrastructure.Import;

/// <summary>Imports mRemoteNG 1.75+ connection XML without retaining plaintext secrets in profiles.</summary>
public sealed class MRemoteNgXmlImporter
{
    private readonly MRemoteNgAeadDecryptor _decryptor;

    public MRemoteNgXmlImporter(MRemoteNgAeadDecryptor? decryptor = null)
    {
        _decryptor = decryptor ?? new MRemoteNgAeadDecryptor();
    }

    public MRemoteNgImportResult Import(string xml, string password, VaultId vaultId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var document = XDocument.Parse(xml, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("The mRemoteNG document has no root node.");
        var iterations = ReadInteger(root, "KdfIterations", 1000);
        var protectedMarker = Attribute(root, "Protected");
        if (!string.IsNullOrWhiteSpace(protectedMarker))
        {
            _ = _decryptor.Decrypt(protectedMarker, password, iterations);
        }

        var folders = new List<ConnectionFolder>();
        var connections = new List<ConnectionProfile>();
        var cards = new List<ImportedIdentityCard>();
        var warnings = new List<string>();
        var deduplicatedCards = new Dictionary<string, ImportedIdentityCard>(StringComparer.Ordinal);

        foreach (var node in root.Elements("Node"))
        {
            ImportNode(node, null, InheritedValues.Empty, password, iterations, vaultId,
                folders, connections, cards, warnings, deduplicatedCards);
        }

        return new MRemoteNgImportResult
        {
            Folders = folders,
            Connections = connections,
            IdentityCards = cards,
            Warnings = warnings,
        };
    }

    private void ImportNode(
        XElement node,
        FolderId? parentFolderId,
        InheritedValues inherited,
        string password,
        int iterations,
        VaultId vaultId,
        List<ConnectionFolder> folders,
        List<ConnectionProfile> connections,
        List<ImportedIdentityCard> cards,
        List<string> warnings,
        Dictionary<string, ImportedIdentityCard> deduplicatedCards)
    {
        var type = Attribute(node, "Type");
        var name = Attribute(node, "Name") ?? "Imported";
        if (string.Equals(type, "Container", StringComparison.OrdinalIgnoreCase))
        {
            var folderId = FolderId.New();
            var folderValues = Apply(node, inherited, password, iterations);
            var protocolCredentials = new Dictionary<string, ConnectionCredentialReference>(StringComparer.OrdinalIgnoreCase);
            folders.Add(new ConnectionFolder
            {
                Id = folderId,
                Name = name,
                ParentId = parentFolderId,
                ProtocolCredentials = protocolCredentials,
            });

            foreach (var child in node.Elements("Node"))
            {
                ImportNode(child, folderId, folderValues, password, iterations, vaultId,
                    folders, connections, cards, warnings, deduplicatedCards);
            }

            var folderIndex = folders.FindIndex(folder => folder.Id == folderId);
            folders[folderIndex] = folders[folderIndex] with
            {
                ProtocolCredentials = protocolCredentials,
            };
            return;
        }

        if (!string.Equals(type, "Connection", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Skipped unsupported mRemoteNG node '{name}' ({type ?? "unknown"}).");
            return;
        }

        var values = Apply(node, inherited, password, iterations);
        if (!TryMapProtocol(values.Protocol, out var protocolId, out var scheme, out var defaultPort))
        {
            warnings.Add($"Skipped '{name}' because protocol '{values.Protocol}' is not supported.");
            return;
        }

        var host = Attribute(node, "Hostname");
        if (string.IsNullOrWhiteSpace(host))
        {
            warnings.Add($"Skipped '{name}' because it has no hostname.");
            return;
        }

        var port = values.Port > 0 ? values.Port : defaultPort;
        var endpoint = new UriBuilder(scheme, host, port).Uri;
        var reference = ConnectionCredentialReference.None;
        if (!string.IsNullOrWhiteSpace(values.Username) && !string.IsNullOrEmpty(values.Password))
        {
            var card = GetOrCreateCard(name, protocolId, values, vaultId, cards, deduplicatedCards);
            reference = ConnectionCredentialReference.IdentityCard(vaultId, card.Definition.Id);

            if (parentFolderId is { } folderId && CredentialsAreInherited(node))
            {
                var folderIndex = folders.FindIndex(folder => folder.Id == folderId);
                var map = (Dictionary<string, ConnectionCredentialReference>)folders[folderIndex].ProtocolCredentials;
                map.TryAdd(protocolId, reference);
                reference = ConnectionCredentialReference.Inherited;
            }
        }

        connections.Add(new ConnectionProfile
        {
            Id = ConnectionId.New(),
            Name = name,
            Endpoint = endpoint,
            ProtocolId = protocolId,
            FolderId = parentFolderId,
            Credential = reference,
            ProtocolSettings = BuildProtocolSettings(node, protocolId),
        });
    }

    private ImportedIdentityCard GetOrCreateCard(
        string connectionName,
        string protocolId,
        InheritedValues values,
        VaultId vaultId,
        List<ImportedIdentityCard> cards,
        Dictionary<string, ImportedIdentityCard> deduplicatedCards)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(values.Password!);
        var fingerprintSource = Encoding.UTF8.GetBytes($"{protocolId}\0{values.Username}\0{values.Domain}\0");
        var combined = new byte[fingerprintSource.Length + passwordBytes.Length];
        fingerprintSource.CopyTo(combined, 0);
        passwordBytes.CopyTo(combined, fingerprintSource.Length);
        var key = Convert.ToHexString(SHA256.HashData(combined));
        CryptographicOperations.ZeroMemory(fingerprintSource);
        CryptographicOperations.ZeroMemory(combined);

        if (deduplicatedCards.TryGetValue(key, out var existing))
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            return existing;
        }

        var credentialId = new CredentialId(Guid.NewGuid());
        var displayName = string.IsNullOrWhiteSpace(values.Domain)
            ? $"{values.Username} · {protocolId.ToUpperInvariant()}"
            : $"{values.Domain}\\{values.Username} · {protocolId.ToUpperInvariant()}";
        var definition = new CredentialDefinition
        {
            Id = credentialId,
            VaultId = vaultId,
            Name = displayName,
            Kind = CredentialKind.UsernamePassword,
            ProtocolScope = protocolId,
            Username = values.Username,
            Domain = values.Domain,
        };
        var imported = new ImportedIdentityCard
        {
            Definition = definition,
            IdentityCard = new IdentityCard
            {
                VaultId = vaultId,
                CredentialId = credentialId,
                Name = displayName,
                ProtocolId = protocolId,
                Username = values.Username!,
                Domain = values.Domain,
            },
            Secret = passwordBytes,
        };
        deduplicatedCards.Add(key, imported);
        cards.Add(imported);
        return imported;
    }

    private InheritedValues Apply(XElement node, InheritedValues parent, string password, int iterations)
    {
        var username = IsInherited(node, "Username") ? parent.Username : Attribute(node, "Username");
        var domain = IsInherited(node, "Domain") ? parent.Domain : Attribute(node, "Domain");
        var encryptedPassword = IsInherited(node, "Password") ? null : Attribute(node, "Password");
        var clearPassword = encryptedPassword is null
            ? parent.Password
            : _decryptor.Decrypt(encryptedPassword, password, iterations);
        var protocol = IsInherited(node, "Protocol") ? parent.Protocol : Attribute(node, "Protocol");
        var port = IsInherited(node, "Port") ? parent.Port : ReadInteger(node, "Port", parent.Port);
        return new(username, domain, clearPassword, protocol, port);
    }

    private static bool CredentialsAreInherited(XElement node) =>
        IsInherited(node, "Username") && IsInherited(node, "Domain") && IsInherited(node, "Password");

    private static bool IsInherited(XElement node, string field) =>
        bool.TryParse(Attribute(node, $"Inherit{field}"), out var inherited) && inherited;

    private static ProtocolSettings BuildProtocolSettings(XElement node, string protocolId)
    {
        var settings = new ProtocolSettings();
        if (protocolId == "rdp")
        {
            settings = settings.Set("UseConsoleSession", (Attribute(node, "UseConsoleSession") ?? "false").ToLowerInvariant());
        }
        return settings;
    }

    private static bool TryMapProtocol(string? source, out string id, out string scheme, out int defaultPort)
    {
        (id, scheme, defaultPort) = (source?.ToUpperInvariant()) switch
        {
            "RDP" => ("rdp", "rdp", 3389),
            "VNC" => ("vnc", "vnc", 5900),
            "SSH2" => ("ssh2", "ssh", 22),
            "HTTP" => ("http", "http", 80),
            "HTTPS" => ("https", "https", 443),
            "TELNET" => ("terminal", "telnet", 23),
            _ => (string.Empty, string.Empty, 0),
        };
        return id.Length > 0;
    }

    private static string? Attribute(XElement element, string name) =>
        element.Attribute(name)?.Value;

    private static int ReadInteger(XElement element, string name, int fallback) =>
        int.TryParse(Attribute(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private sealed record InheritedValues(string? Username, string? Domain, string? Password, string? Protocol, int Port)
    {
        public static InheritedValues Empty { get; } = new(null, null, null, null, 0);
    }
}
