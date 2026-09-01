using Remote.Application.Connections;
using Remote.Application.Vaults;
using Remote.Infrastructure.Security;

namespace Remote.Infrastructure.Vaults;

/// <summary>An unlocked in-memory Vault that owns and clears all secret buffers.</summary>
public sealed class CredentialVault : IDisposable
{
    public const int MaximumRetainedVersions = 5;

    private readonly Dictionary<CredentialId, VaultEntry> _entries = [];
    private bool _isLocked;

    public bool IsLocked => _isLocked;

    public IReadOnlyList<CredentialDefinition> Credentials
    {
        get
        {
            EnsureUnlocked();
            return _entries.Values.Select(entry => entry.Definition).ToArray();
        }
    }

    public void Add(CredentialDefinition definition, ReadOnlySpan<byte> secret)
    {
        EnsureUnlocked();
        ArgumentNullException.ThrowIfNull(definition);
        if (secret.IsEmpty)
        {
            throw new ArgumentException("Credential secret cannot be empty.", nameof(secret));
        }

        if (!_entries.TryAdd(definition.Id, new VaultEntry(definition, new SensitiveBuffer(secret))))
        {
            throw new InvalidOperationException($"Credential '{definition.Id.Value}' already exists.");
        }
    }

    public void Update(CredentialDefinition definition, ReadOnlySpan<byte> secret)
    {
        EnsureUnlocked();
        ArgumentNullException.ThrowIfNull(definition);
        if (!_entries.TryGetValue(definition.Id, out var entry))
        {
            throw new KeyNotFoundException($"Credential '{definition.Id.Value}' was not found.");
        }

        entry.Update(definition, secret);
    }

    public byte[] Reveal(CredentialId credentialId)
    {
        EnsureUnlocked();
        return GetEntry(credentialId).Current.Copy();
    }

    public int GetVersionCount(CredentialId credentialId)
    {
        EnsureUnlocked();
        return GetEntry(credentialId).VersionCount;
    }

    public bool Delete(CredentialId credentialId)
    {
        EnsureUnlocked();
        if (!_entries.Remove(credentialId, out var entry))
        {
            return false;
        }

        entry.Dispose();
        return true;
    }

    public void Lock()
    {
        if (_isLocked)
        {
            return;
        }

        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }

        _entries.Clear();
        _isLocked = true;
    }

    public void Dispose() => Lock();

    private VaultEntry GetEntry(CredentialId credentialId) =>
        _entries.TryGetValue(credentialId, out var entry)
            ? entry
            : throw new KeyNotFoundException($"Credential '{credentialId.Value}' was not found.");

    private void EnsureUnlocked()
    {
        if (_isLocked)
        {
            throw new VaultLockedException();
        }
    }

    private sealed class VaultEntry : IDisposable
    {
        private readonly Queue<SensitiveBuffer> _previousVersions = [];

        public VaultEntry(CredentialDefinition definition, SensitiveBuffer current)
        {
            Definition = definition;
            Current = current;
        }

        public CredentialDefinition Definition { get; private set; }

        public SensitiveBuffer Current { get; private set; }

        public int VersionCount => _previousVersions.Count + 1;

        public void Update(CredentialDefinition definition, ReadOnlySpan<byte> secret)
        {
            if (secret.IsEmpty)
            {
                throw new ArgumentException("Credential secret cannot be empty.", nameof(secret));
            }

            _previousVersions.Enqueue(Current);
            while (_previousVersions.Count >= MaximumRetainedVersions)
            {
                _previousVersions.Dequeue().Dispose();
            }

            Current = new SensitiveBuffer(secret);
            Definition = definition;
        }

        public void Dispose()
        {
            Current.Dispose();
            while (_previousVersions.TryDequeue(out var version))
            {
                version.Dispose();
            }
        }
    }
}

public sealed class VaultLockedException()
    : InvalidOperationException("The Vault is locked.");
