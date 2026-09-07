using System.Globalization;
using ArkAscendedServerAdmin.Ini;

namespace ArkAscendedServerAdmin.Rcon;

/// <summary>
/// Reads the RCON endpoint out of generated <c>GameUserSettings.ini</c> text — the file the running process
/// actually read, so it is the source at launch and at attach alike (plan step 23).
/// </summary>
public static class RconCredentials
{
    /// <summary>Returns null (with a reason) when <c>ServerAdminPassword</c> is missing or empty or <c>RCONPort</c> is unusable.</summary>
    public static RconEndpoint? TryRead(string gameUserSettingsText, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(gameUserSettingsText);

        var ini = IniText.Parse(gameUserSettingsText);
        var password = ini.Get(IniGenerator.ServerSettingsSection, "ServerAdminPassword");
        if (string.IsNullOrWhiteSpace(password))
        {
            problem = "ServerAdminPassword under [ServerSettings] is missing or empty; RCON is the only stop path, so it is required.";
            return null;
        }

        var portText = ini.Get(IniGenerator.ServerSettingsSection, "RCONPort");
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            problem = $"RCONPort under [ServerSettings] is missing or invalid (was '{portText}').";
            return null;
        }

        problem = null;
        return new RconEndpoint(port, password);
    }
}
