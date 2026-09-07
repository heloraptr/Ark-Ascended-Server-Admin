using System.Globalization;
using System.Text.RegularExpressions;

namespace ArkAscendedServerAdmin.Infrastructure.Processes;

/// <summary>
/// Extracts the TCP ports the web UI binds from <c>HostConfiguration.BindUrls</c> (Kestrel endpoint URLs
/// such as <c>https://localhost:5001</c> or <c>http://+:5000</c>) so the launch-time port check (plan
/// step 15) can refuse an instance whose ports collide with the manager's own listener.
/// </summary>
public static partial class KestrelPorts
{
    public static IReadOnlyList<int> Parse(IEnumerable<string> bindUrls)
    {
        ArgumentNullException.ThrowIfNull(bindUrls);

        var ports = new List<int>();
        foreach (var url in bindUrls)
        {
            if (TryParse(url, out var port) && !ports.Contains(port))
            {
                ports.Add(port);
            }
        }

        return ports;
    }

    private static bool TryParse(string? url, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var normalized = url.Trim().Replace("://+", "://localhost", StringComparison.Ordinal).Replace("://*", "://localhost", StringComparison.Ordinal);
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Port > 0)
        {
            port = uri.Port;
            return true;
        }

        var match = TrailingPort().Match(normalized);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port > 0;
    }

    [GeneratedRegex(@":(\d{1,5})/?$")]
    private static partial Regex TrailingPort();
}
