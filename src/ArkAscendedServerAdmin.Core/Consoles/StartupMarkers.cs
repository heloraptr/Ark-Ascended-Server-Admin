namespace ArkAscendedServerAdmin.Consoles;

/// <summary>Lifecycle events derived from <c>ShooterGame.log</c> lines (plan step 22, Spike A).</summary>
public enum StartupMarker
{
    /// <summary><c>Full Startup: N seconds</c> — engine init done, world loaded (+20–55 s).</summary>
    WorldLoaded,
    /// <summary><c>Server has completed startup and is now advertising for join.</c> — RCON becomes fast (+65–95 s).</summary>
    Advertising,
    /// <summary><c>Log file closed</c> — clean shutdown.</summary>
    LogClosed,
}

/// <summary>
/// Classifies log lines. <c>Server: "..." has successfully started!</c> (+5 s) deliberately maps to
/// nothing: the world is not loaded yet. Markers only advance the Starting phase; promotion to Running is
/// the RCON probe's job.
/// </summary>
public static class StartupMarkers
{
    public const string WorldLoadedText = "Full Startup:";
    public const string AdvertisingText = "Server has completed startup and is now advertising for join";
    public const string LogClosedText = "Log file closed";

    public static StartupMarker? Classify(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.Contains(AdvertisingText, StringComparison.Ordinal))
        {
            return StartupMarker.Advertising;
        }

        if (line.Contains(WorldLoadedText, StringComparison.Ordinal))
        {
            return StartupMarker.WorldLoaded;
        }

        if (line.Contains(LogClosedText, StringComparison.Ordinal))
        {
            return StartupMarker.LogClosed;
        }

        return null;
    }
}
