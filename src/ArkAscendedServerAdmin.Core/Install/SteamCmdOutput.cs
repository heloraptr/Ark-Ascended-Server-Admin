using System.Globalization;
using System.Text.RegularExpressions;

namespace ArkAscendedServerAdmin.Install;

/// <summary>One SteamCMD progress line, e.g. <c>Update state (0x61) downloading, progress: 15.67 (1912380961 / 12206318952)</c>.</summary>
/// <param name="State">The state word after the hex code ("downloading", "verifying", "reconfiguring", ...).</param>
/// <param name="Percent">The reported percentage (0–100).</param>
/// <param name="Done">Bytes done so far.</param>
/// <param name="Total">Total bytes for this state; 0 while reconfiguring.</param>
public sealed record SteamCmdProgress(string State, double Percent, long Done, long Total);

/// <summary>
/// Pure parser for the SteamCMD output lines the manager cares about (plan step 20, Phase 2 spike):
/// progress lines for a bar, the success line, and the first-run self-update line that precedes exit
/// code 7.
/// </summary>
public static partial class SteamCmdOutput
{
    /// <summary>The line SteamCMD prints after updating itself; the process then exits with code 7 without running the script.</summary>
    public const string SelfUpdateMarker = "Update complete, launching...";

    public static bool TryParseProgress(string line, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SteamCmdProgress? progress)
    {
        ArgumentNullException.ThrowIfNull(line);
        var match = ProgressPattern().Match(line);
        if (match.Success
            && double.TryParse(match.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            && long.TryParse(match.Groups["done"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var done)
            && long.TryParse(match.Groups["total"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
        {
            progress = new SteamCmdProgress(match.Groups["state"].Value, percent, done, total);
            return true;
        }

        progress = null;
        return false;
    }

    /// <summary>True for <c>Success! App '&lt;appId&gt;' fully installed.</c></summary>
    public static bool IsSuccess(string line, int appId)
    {
        ArgumentNullException.ThrowIfNull(line);
        var match = SuccessPattern().Match(line);
        return match.Success
            && int.TryParse(match.Groups["app"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            && id == appId;
    }

    /// <summary>True for the <see cref="SelfUpdateMarker"/> line.</summary>
    public static bool IsSelfUpdate(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.AsSpan().Trim().StartsWith(SelfUpdateMarker, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^\s*Update state \(0x[0-9A-Fa-f]+\) (?<state>[^,]+), progress: (?<percent>-?\d+(?:\.\d+)?) \((?<done>\d+) / (?<total>\d+)\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressPattern();

    [GeneratedRegex(@"^\s*Success! App '(?<app>\d+)' fully installed\.\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SuccessPattern();
}

/// <summary>
/// Read-only view of the running SteamCMD's progress for the console panel (plan step 20 / Phase 5).
/// <see cref="Current"/> is the last parsed progress line of the current run and null when SteamCMD is
/// not running or has not printed one yet; <see cref="Changed"/> is raised on a background thread each
/// time it changes (including the reset to null when a run ends).
/// </summary>
public interface ISteamCmdProgressMonitor
{
    SteamCmdProgress? Current { get; }

    event Action<SteamCmdProgress?>? Changed;
}

/// <summary>
/// The retry budget for SteamCMD runs (plan step 20): <see cref="MaxAttempts"/> attempts, waiting
/// <see cref="InitialDelay"/> after the first failure and doubling up to <see cref="MaxDelay"/>
/// (30 s, 60 s, 120 s, 240 s between the five default attempts). Exit code 7 is handled outside this
/// budget.
/// </summary>
public sealed class SteamCmdRetryPolicy
{
    public const int DefaultMaxAttempts = 5;

    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromMinutes(8);

    public SteamCmdRetryPolicy()
        : this(DefaultInitialDelay, DefaultMaxDelay, DefaultMaxAttempts)
    {
    }

    public SteamCmdRetryPolicy(TimeSpan initialDelay, TimeSpan maxDelay, int maxAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, initialDelay);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        InitialDelay = initialDelay;
        MaxDelay = maxDelay;
        MaxAttempts = maxAttempts;
    }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaxDelay { get; }

    public int MaxAttempts { get; }

    /// <summary>
    /// The wait before the next attempt once <paramref name="failedAttempts"/> have failed, or null when
    /// the budget is spent (<paramref name="failedAttempts"/> ≥ <see cref="MaxAttempts"/>).
    /// </summary>
    public TimeSpan? DelayAfter(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
        if (failedAttempts >= MaxAttempts)
        {
            return null;
        }

        var ticks = InitialDelay.Ticks * Math.Pow(2, failedAttempts - 1);
        return ticks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks((long)ticks);
    }
}
