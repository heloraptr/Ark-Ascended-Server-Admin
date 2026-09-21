namespace ArkAscendedServerAdmin.Processes;

/// <summary>
/// The arithmetic behind the resource telemetry (B7), kept pure so it can be tested without a process: the CPU share
/// from two <c>TotalProcessorTime</c> readings, and the publish throttle.
/// </summary>
public static class TelemetrySampler
{
    /// <summary>The least time between two published samples for one instance.</summary>
    public static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The share of the whole box's CPU a process used between two readings, 0 to 100: the processor time it consumed
    /// over <paramref name="elapsed"/>, divided by <paramref name="processorCount"/>, so a process saturating one of
    /// four cores reads 25. Zero when the readings cannot be compared (no elapsed time, no processors, or a total that
    /// went backwards); capped at 100 when timer skew makes the consumed time exceed the wall clock.
    /// </summary>
    public static double CpuPercent(TimeSpan previousTotal, TimeSpan currentTotal, TimeSpan elapsed, int processorCount)
    {
        if (elapsed <= TimeSpan.Zero || processorCount <= 0)
        {
            return 0;
        }

        var used = currentTotal - previousTotal;
        if (used <= TimeSpan.Zero)
        {
            return 0;
        }

        var percent = used.Ticks * 100.0 / ((double)elapsed.Ticks * processorCount);
        return Math.Min(percent, 100);
    }

    /// <summary>True when nothing has been published yet, or the last sample is at least <paramref name="interval"/> old.</summary>
    public static bool ShouldPublish(DateTimeOffset? lastPublishedAt, DateTimeOffset now, TimeSpan interval) =>
        lastPublishedAt is null || now - lastPublishedAt.Value >= interval;
}
