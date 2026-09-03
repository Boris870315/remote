namespace Remote.Infrastructure.Protocols.Rdp;

public static class RdpLoginName
{
    public static string? Format(string? username, string? domain)
    {
        username = username?.Trim();
        domain = domain?.Trim();
        if (string.IsNullOrEmpty(username))
        {
            return null;
        }

        // A down-level logon name or UPN already contains its intended scope.
        if (username.Contains('\\') || username.Contains('@') || string.IsNullOrEmpty(domain))
        {
            return username;
        }

        return $"{domain}\\{username}";
    }
}
