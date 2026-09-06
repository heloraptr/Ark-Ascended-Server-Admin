namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests;

/// <summary>
/// Placeholder until Phase 4 lands Infrastructure code; proves the Windows test project wires up.
/// </summary>
public class PlatformTests
{
    [Fact]
    public void RunsOnWindows() => Assert.True(OperatingSystem.IsWindows());
}
