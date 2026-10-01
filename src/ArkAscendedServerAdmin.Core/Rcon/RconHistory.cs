namespace ArkAscendedServerAdmin.Rcon;

/// <summary>
/// The commands typed into one instance's console, kept in <c>Instances\&lt;slug&gt;\rcon-history.txt</c> so the
/// arrow keys reach them from any browser and after a restart. Only commands sent from the console input are
/// recorded; the manager's own probes, schedules, and saves never are.
/// </summary>
public interface IRconHistoryStore
{
    /// <summary>The history of the instance, oldest first; empty when there is no file yet.</summary>
    Task<IReadOnlyList<string>> LoadAsync(string instanceSlug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records <paramref name="command"/> by rereading the file, appending, and replacing it atomically, all while
    /// holding the instance lease, so a delete (which holds the same lease) can never find a half-written file in
    /// the folder it is removing. Returns the history as it now stands, or null when the command was not recorded
    /// because a lifecycle operation (stop, backup, delete) held the lease past a short wait. Nothing is written
    /// when the instance folder does not exist, and a command <see cref="RconHistory.Clean"/> refuses or that
    /// repeats the newest entry leaves the file unchanged.
    /// </summary>
    Task<IReadOnlyList<string>?> AppendAsync(int instanceId, string instanceSlug, string command, CancellationToken cancellationToken = default);
}

/// <summary>The rules of the history file, shared by the store and its tests.</summary>
public static class RconHistory
{
    /// <summary>Lines kept; the oldest go first.</summary>
    public const int Capacity = 200;

    /// <summary>Longest command kept; the same bound the scheduled RCON actions use.</summary>
    public const int MaxCommandLength = RconCommands.MaxCommandLength;

    /// <summary>
    /// How much of the end of the file is read. 200 lines of 512 characters can exceed it, but real commands are
    /// short; the bound only stops a huge hand-edited file from costing anything.
    /// </summary>
    public const int MaxBytesRead = 64 * 1024;

    /// <summary>
    /// A command as stored: trimmed. Null when it is empty, contains a line break, or is longer than
    /// <see cref="MaxCommandLength"/>; such commands are skipped, never shortened.
    /// </summary>
    public static string? Clean(string? command)
    {
        var text = (command ?? string.Empty).Trim();
        return text.Length is 0 or > MaxCommandLength || text.AsSpan().IndexOfAny('\r', '\n') >= 0 ? null : text;
    }

    /// <summary>
    /// Reads file lines the way a hand-edited file needs: blank and overlong lines are skipped, a line equal to
    /// the one before it is dropped, and only the newest <see cref="Capacity"/> are kept.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new List<string>();
        foreach (var line in lines)
        {
            Append(result, line);
        }

        return result;
    }

    /// <summary>
    /// Appends <paramref name="command"/> to <paramref name="history"/> in place: refused (false) when
    /// <see cref="Clean"/> rejects it or it equals the newest entry; the oldest entries are dropped past
    /// <see cref="Capacity"/>.
    /// </summary>
    public static bool Append(List<string> history, string? command)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (Clean(command) is not { } text || (history.Count > 0 && string.Equals(history[^1], text, StringComparison.Ordinal)))
        {
            return false;
        }

        history.Add(text);
        if (history.Count > Capacity)
        {
            history.RemoveRange(0, history.Count - Capacity);
        }

        return true;
    }
}
