using ArkAscendedServerAdmin.Components.Shared;
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

    [Fact]
    public void Telemetry_IsTheRamAndCpuLine()
    {
        var sample = new InstanceTelemetry(1, 6_657_199_309L, 14.2, DateTimeOffset.UtcNow);

        Assert.Equal("RAM 6.2 GB · CPU 14 %", Presentation.Telemetry(sample));
    }
}
