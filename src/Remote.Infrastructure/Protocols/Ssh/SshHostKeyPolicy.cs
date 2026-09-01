namespace Remote.Infrastructure.Protocols.Ssh;

public static class SshHostKeyPolicy
{
    public static bool CanTrust(
        SshHostKeyInfo presented,
        string? expectedSha256,
        Func<SshHostKeyInfo, bool>? confirmUnknown)
    {
        ArgumentNullException.ThrowIfNull(presented);
        var normalizedExpected = Normalize(expectedSha256);
        var normalizedPresented = Normalize(presented.Sha256Fingerprint);
        if (normalizedExpected is not null)
        {
            return string.Equals(normalizedExpected, normalizedPresented, StringComparison.Ordinal);
        }

        return confirmUnknown?.Invoke(presented) is true;
    }

    private static string? Normalize(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        const string prefix = "SHA256:";
        var value = fingerprint.Trim();
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
    }
}
