using ArkAscendedServerAdmin.Infrastructure.Processes;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Processes;

public class KestrelPortsTests
{
    [Fact]
    public void ParsesExplicitWildcardAndDefaultPorts()
    {
        var ports = KestrelPorts.Parse(["https://localhost:5001", "http://+:5000", "http://*:5000/", "https://example.org", " ", "not a url"]);

        Assert.Equal([5001, 5000, 443], ports);
    }

    [Fact]
    public void EmptyList_YieldsNoPorts() => Assert.Empty(KestrelPorts.Parse([]));
}
