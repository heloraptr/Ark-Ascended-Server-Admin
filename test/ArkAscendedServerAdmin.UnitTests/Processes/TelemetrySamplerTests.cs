using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.UnitTests.Processes;

/// <summary>The CPU-percent arithmetic and the publish throttle behind the resource telemetry (B7).</summary>
public class TelemetrySamplerTests
{
    private static readonly TimeSpan _previousTotal = TimeSpan.FromMinutes(3);

    [Theory]
    [InlineData(1.0, 2.0, 4, 12.5)]
    [InlineData(2.0, 2.0, 1, 100.0)]
    [InlineData(8.0, 2.0, 4, 100.0)]
    [InlineData(0.5, 2.0, 8, 3.125)]
    [InlineData(0.0, 2.0, 4, 0.0)]
    public void CpuPercent_IsTheProcessorTimeShareOfTheWholeBox(double usedSeconds, double elapsedSeconds, int processorCount, double expected)
    {
        var current = _previousTotal + TimeSpan.FromSeconds(usedSeconds);

        var percent = TelemetrySampler.CpuPercent(_previousTotal, current, TimeSpan.FromSeconds(elapsedSeconds), processorCount);

        Assert.Equal(expected, percent, precision: 6);
    }

    [Fact]
    public void CpuPercent_IsCappedAt100_WhenTheConsumedTimeExceedsTheWallClock()
    {
        var current = _previousTotal + TimeSpan.FromSeconds(3);

        Assert.Equal(100.0, TelemetrySampler.CpuPercent(_previousTotal, current, TimeSpan.FromSeconds(2), 1));
    }

    [Fact]
    public void CpuPercent_IsZero_WhenTheReadingsCannotBeCompared()
    {
        var current = _previousTotal + TimeSpan.FromSeconds(1);

        Assert.Equal(0.0, TelemetrySampler.CpuPercent(_previousTotal, current, TimeSpan.Zero, 4));
        Assert.Equal(0.0, TelemetrySampler.CpuPercent(_previousTotal, current, TimeSpan.FromSeconds(-1), 4));
        Assert.Equal(0.0, TelemetrySampler.CpuPercent(_previousTotal, current, TimeSpan.FromSeconds(2), 0));
        Assert.Equal(0.0, TelemetrySampler.CpuPercent(current, _previousTotal, TimeSpan.FromSeconds(2), 4));
    }

    [Fact]
    public void ShouldPublish_AlwaysForTheFirstSample_ThenOnlyOnceTheIntervalHasPassed()
    {
        var published = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        Assert.True(TelemetrySampler.ShouldPublish(null, published, TelemetrySampler.PublishInterval));
        Assert.False(TelemetrySampler.ShouldPublish(published, published + TimeSpan.FromSeconds(2), TelemetrySampler.PublishInterval));
        Assert.False(TelemetrySampler.ShouldPublish(published, published + TimeSpan.FromSeconds(4.9), TelemetrySampler.PublishInterval));
        Assert.True(TelemetrySampler.ShouldPublish(published, published + TimeSpan.FromSeconds(5), TelemetrySampler.PublishInterval));
        Assert.True(TelemetrySampler.ShouldPublish(published, published + TimeSpan.FromSeconds(6), TelemetrySampler.PublishInterval));
    }
}
