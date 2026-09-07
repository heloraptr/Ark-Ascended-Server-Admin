namespace ArkAscendedServerAdmin.Rcon;

/// <summary>Loopback RCON endpoint of one instance; read from the generated <c>GameUserSettings.ini</c> (plan step 23).</summary>
public sealed record RconEndpoint(int Port, string Password);

public enum RconFailure
{
    /// <summary>TCP connect refused or reset (the process is gone or RCON is not listening yet).</summary>
    Connect,
    /// <summary>The server rejected the password.</summary>
    Authentication,
    /// <summary>Connect, auth, or the command exceeded the timeout.</summary>
    Timeout,
    /// <summary>Anything else the client could not interpret.</summary>
    Protocol,
}

public sealed class RconException(RconFailure failure, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public RconFailure Failure { get; } = failure;
}

/// <summary>
/// Executes one RCON command per connection (plan step 23): connect, authenticate, send, read, dispose. A
/// new connection per call is deliberate — the server is slow (auth 5–7 s, commands 2–5 s) until it is
/// advertising, and holding sockets open across that gains nothing. Every failure is an
/// <see cref="RconException"/> so callers can fall through to their next step.
/// </summary>
public interface IRconClient
{
    Task<string> ExecuteAsync(RconEndpoint endpoint, string command, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>The commands the manager uses and the replies Spike A observed.</summary>
public static class RconCommands
{
    public const string SaveWorld = "saveworld";
    public const string DoExit = "doexit";
    public const string ListPlayers = "ListPlayers";

    /// <summary>Reply to <see cref="SaveWorld"/>; the <c>.ark</c> write begins ~0.8 s after it (plan step 28).</summary>
    public const string SaveWorldReply = "World Saved";

    /// <summary>Reply to <see cref="DoExit"/>; the process exits ~26 s later with code -1, which is normal.</summary>
    public const string DoExitReply = "Exiting...";

    public const string NoPlayersReply = "No Players Connected";

    public static string Broadcast(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return $"broadcast {message.ReplaceLineEndings(" ")}";
    }
}
