namespace ArkAscendedServerAdmin.CurseForge;

/// <summary>Vets links from the CurseForge API before they are stored and rendered as an href.</summary>
public static class CurseForgeLinks
{
    /// <summary>Matches the column length on <c>ModLibrary.WebsiteUrl</c>.</summary>
    public const int MaxLength = 500;

    /// <summary>
    /// Returns <paramref name="url"/> when it is an absolute https URL on curseforge.com or one of its
    /// subdomains, and null for anything else (other hosts, other schemes, relative or malformed values).
    /// </summary>
    public static string? SafeWebsiteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxLength)
            return null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            return null;

        var host = uri.IdnHost;
        var onCurseForge = host.Equals("curseforge.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".curseforge.com", StringComparison.OrdinalIgnoreCase);

        return onCurseForge ? uri.AbsoluteUri : null;
    }
}
