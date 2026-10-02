using ArkAscendedServerAdmin.Components.Shared;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Components;

/// <summary>
/// The telemetry formatters (B7). They live in the Components project, which only this test project reaches (through
/// the Server reference), so they are checked here rather than in the Core-only unit tests.
/// </summary>
public class PresentationTelemetryTests
{
    [Theory]
    [InlineData(6_657_199_309L, "6.2 GB")]
    [InlineData(13_636_521_166L, "12.7 GB")]
    [InlineData(536_870_912L, "0.5 GB")]
    [InlineData(0L, "0.0 GB")]
    public void Gigabytes_HasOneDecimal_AndIsAlwaysInGigabytes(long bytes, string expected) =>
        Assert.Equal(expected, Presentation.Gigabytes(bytes));

    [Theory]
    [InlineData(14.2, "14 %")]
    [InlineData(14.5, "15 %")]
    [InlineData(0.0, "0 %")]
    [InlineData(99.6, "100 %")]
    [InlineData(100.0, "100 %")]
    public void Percent_IsAWholeNumber(double percent, string expected) =>
        Assert.Equal(expected, Presentation.Percent(percent));

    /// <summary>B4: Crashed is a solid red lamp labelled "Crashed", and its hint is the reason the manager recorded.</summary>
    [Fact]
    public void Crashed_IsSolidRed_WithItsDetailAsTheHint()
    {
        var runtime = new InstanceRuntime(1, InstanceState.Crashed, null, null, null, null, "Automatic restart was refused: Port conflict.", AutoRestarts: 2);

        Assert.Equal("st-bad", Presentation.Tone(InstanceState.Crashed));
        Assert.False(Presentation.IsHollow(InstanceState.Crashed));
        Assert.Equal("Crashed", Presentation.Label(InstanceState.Crashed));
        Assert.Equal("Automatic restart was refused: Port conflict.", Presentation.Hint(runtime));
        Assert.NotNull(Presentation.Hint(runtime with { Detail = null }));
        Assert.False(runtime.HasLiveProcess);
    }

    [Theory]
    [InlineData(0, "Crashed")]
    [InlineData(1, "Crashed · 1 restart")]
    [InlineData(3, "Crashed · 3 restarts")]
    public void CrashedLabel_CarriesTheAutomaticRestartCount(int restarts, string expected)
    {
        var runtime = new InstanceRuntime(1, InstanceState.Crashed, null, null, null, null, null, AutoRestarts: restarts);

        Assert.Equal(expected, Presentation.Label(runtime));
        Assert.Equal("Stopped", Presentation.Label(runtime with { State = InstanceState.Stopped }));
    }

    [Fact]
    public void Telemetry_IsTheRamAndCpuLine()
    {
        var sample = new InstanceTelemetry(1, 6_657_199_309L, 14.2, DateTimeOffset.UtcNow);

        Assert.Equal("RAM 6.2 GB · CPU 14 %", Presentation.Telemetry(sample));
    }
}
