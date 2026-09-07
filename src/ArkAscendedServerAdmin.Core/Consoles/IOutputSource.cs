namespace ArkAscendedServerAdmin.Consoles;

/// <summary>One line produced by an output source.</summary>
/// <param name="Text">The line without its terminator.</param>
/// <param name="ObservedAt">When the source saw it (not the game's own timestamp).</param>
/// <param name="IsBackfill">True for history read before live following began.</param>
public sealed record OutputLine(string Text, DateTimeOffset ObservedAt, bool IsBackfill);

/// <param name="StartAtEnd">Follow only lines written after the source starts (re-attach); false reads from the top (fresh launch).</param>
/// <param name="BackfillLines">When <paramref name="StartAtEnd"/>, this many trailing lines are delivered first with <see cref="OutputLine.IsBackfill"/> set.</param>
public sealed record OutputSourceOptions(bool StartAtEnd, int BackfillLines);

/// <summary>
/// The single console output source (plan step 14): a polling tail of <c>ShooterGame.log</c>. Opens with
/// <c>FileShare.ReadWrite | FileShare.Delete</c>, polls ~100 ms, waits for the file to appear, and detects
/// rotation by length dropping below the read offset or by NTFS file id — never by creation time (it is
/// tunneled). Stdout is not captured.
/// </summary>
public interface IOutputSource
{
    /// <summary>Runs until <paramref name="cancellationToken"/> fires; <paramref name="onLine"/> is awaited per line.</summary>
    Task RunAsync(OutputSourceOptions options, Func<OutputLine, ValueTask> onLine, CancellationToken cancellationToken);
}

public interface IOutputSourceFactory
{
    IOutputSource ForLogFile(string logPath);
}
