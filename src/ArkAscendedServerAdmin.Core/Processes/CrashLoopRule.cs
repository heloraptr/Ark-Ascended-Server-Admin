namespace ArkAscendedServerAdmin.Processes;

/// <summary>The crash-loop guard's verdict for one unexpected exit (B4): relaunch as attempt N, or give up.</summary>
public abstract record CrashLoopDecision
{
    private CrashLoopDecision()
    {
    }

    /// <summary>Relaunch; <paramref name="Attempt"/> is the new consecutive automatic-restart count (1-based).</summary>
    public sealed record Restart(int Attempt) : CrashLoopDecision;

    /// <summary>The budget is spent; the instance becomes <c>Crashed</c> and waits for a manual Start.</summary>
    public sealed record GiveUp : CrashLoopDecision
    {
        public static readonly GiveUp Instance = new();
    }
}

/// <summary>
/// The crash-loop guard (B4), pure and stateless. It counts consecutive automatic restarts whose session was
/// short-lived rather than restarts inside a sliding window: a modded server that takes minutes to load and dies
/// every few minutes never fits three restarts into a 10-minute window, so a window alone would loop forever.
/// A session that stayed up for <see cref="ShortLived"/> or longer proves the last restart worked, so its exit
/// starts the count again.
/// </summary>
public static class CrashLoopRule
{
    /// <summary>A session that ran at least this long resets the count.</summary>
    public static readonly TimeSpan ShortLived = TimeSpan.FromMinutes(10);

    /// <summary>Consecutive short-lived automatic restarts allowed before giving up.</summary>
    public const int MaxRestarts = 3;

    /// <summary>The detail and console line for an instance the guard gave up on.</summary>
    public static readonly string GaveUpMessage =
        $"The server exited unexpectedly again after {MaxRestarts} automatic restarts; automatic restart gave up. Start it to try again.";

    /// <summary>
    /// The decision for an exit after <paramref name="autoRestarts"/> consecutive automatic restarts, where
    /// <paramref name="uptime"/> is how long the process that just exited ran (its exit time minus its start time).
    /// </summary>
    public static CrashLoopDecision Next(int autoRestarts, TimeSpan uptime)
    {
        var count = uptime >= ShortLived ? 0 : Math.Max(0, autoRestarts);
        return count < MaxRestarts ? new CrashLoopDecision.Restart(count + 1) : CrashLoopDecision.GiveUp.Instance;
    }
}
