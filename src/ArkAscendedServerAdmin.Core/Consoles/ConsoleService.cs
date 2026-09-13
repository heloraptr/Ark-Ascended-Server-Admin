namespace ArkAscendedServerAdmin.Consoles;

/// <summary>Where a console line came from; the panel styles them differently.</summary>
public enum ConsoleLineKind
{
    /// <summary>A line of the game's or SteamCMD's own output.</summary>
    Output,
    /// <summary>A manager note ("re-attached — log history", "sending doexit").</summary>
    Info,
    Warning,
    Error,
    /// <summary>History read from the log on re-attach, not observed live.</summary>
    Backfill,
}

public sealed record ConsoleLine(DateTimeOffset At, string Text, ConsoleLineKind Kind = ConsoleLineKind.Output);

/// <summary>Channel names for <see cref="IConsoleService"/>: one per instance plus the SteamCMD console.</summary>
public static class ConsoleChannels
{
    public const string SteamCmd = "steamcmd";

    public static string Instance(int instanceId) => $"instance:{instanceId}";
}

/// <summary>
/// Per-channel bounded ring buffer of console lines (plan step 22; <see cref="Capacity"/> lines). A
/// singleton fed by the output sources and the SteamCMD runner; Blazor components subscribe to
/// <see cref="LineAppended"/> (raised on a background thread) and marshal to their circuit with
/// <c>InvokeAsync</c>.
/// </summary>
public interface IConsoleService
{
    const int Capacity = 5000;

    /// <summary>A copy of the channel's current lines, oldest first.</summary>
    IReadOnlyList<ConsoleLine> Snapshot(string channel);

    void Append(string channel, ConsoleLine line);

    void Clear(string channel);

    event Action<string, ConsoleLine>? LineAppended;

    /// <summary>Raised after <see cref="Clear"/> so open panels drop their copy of the channel too.</summary>
    event Action<string>? Cleared;
}
