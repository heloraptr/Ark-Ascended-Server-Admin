namespace ArkAscendedServerAdmin.Consoles;

/// <summary>
/// Drops <c>ShooterGame.log</c> lines the owner never needs in the console: the Sentry SDK's chatter
/// (<c>LogSentrySdk:</c>), including the unstamped continuation lines of its request and response dumps
/// (HTTP headers, one per line). Stateful because those continuation lines carry no category of their
/// own: a stamped line (one starting with <c>[</c>) decides whether the block it starts is shown, and
/// every unstamped line that follows inherits that decision. One instance per log tail.
/// </summary>
public sealed class ConsoleNoiseFilter
{
    private const string SentryCategory = "LogSentrySdk:";

    private bool _suppressing;

    /// <summary>True when the line belongs in the console.</summary>
    public bool ShouldShow(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.StartsWith('['))
        {
            _suppressing = line.Contains(SentryCategory, StringComparison.Ordinal);
            return !_suppressing;
        }

        return !_suppressing;
    }
}
