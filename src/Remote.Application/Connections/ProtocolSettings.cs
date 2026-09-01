using System.Collections.Immutable;

namespace Remote.Application.Connections;

/// <summary>Immutable, secret-free protocol-specific Connection settings.</summary>
public sealed class ProtocolSettings
{
    private static readonly string[] ForbiddenKeyFragments =
    [
        "password",
        "passphrase",
        "privatekey",
        "secret",
        "token",
    ];

    private readonly ImmutableDictionary<string, string> _values;

    public ProtocolSettings()
        : this(ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase))
    {
    }

    private ProtocolSettings(ImmutableDictionary<string, string> values)
    {
        _values = values;
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    public ProtocolSettings Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (ForbiddenKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Protocol setting '{key}' could contain secret material. Store a Credential reference instead.",
                nameof(key));
        }

        return new(_values.SetItem(key, value));
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public bool GetBoolean(string key, bool defaultValue = false) =>
        bool.TryParse(Get(key), out var value) ? value : defaultValue;

    public int GetInteger(string key, int defaultValue) =>
        int.TryParse(Get(key), out var value) ? value : defaultValue;
}
