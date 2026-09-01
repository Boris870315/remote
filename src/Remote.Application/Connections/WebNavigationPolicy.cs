namespace Remote.Application.Connections;

public static class WebNavigationPolicy
{
    public static Uri ParseHttpEndpoint(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var candidate = value.Contains("://", StringComparison.Ordinal) ? value : $"https://{value}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Only HTTP or HTTPS addresses without embedded credentials are allowed.", nameof(value));
        }

        return uri;
    }
}
