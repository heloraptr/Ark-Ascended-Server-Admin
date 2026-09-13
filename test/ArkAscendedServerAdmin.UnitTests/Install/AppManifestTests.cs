using ArkAscendedServerAdmin.Install;

namespace ArkAscendedServerAdmin.UnitTests.Install;

public class AppManifestTests
{
    private const string FullyInstalled = """
        "AppState"
        {
        	"appid"		"2430930"
        	"Universe"		"1"
        	"name"		"ARK: Survival Ascended Dedicated Server"
        	"StateFlags"		"4"
        	"installdir"		"ARK Survival Ascended Dedicated Server"
        	"LastUpdated"		"1757000000"
        }
        """;

    [Fact]
    public void StateFlags4_IsFullyInstalled()
    {
        Assert.True(AppManifest.IsFullyInstalled(FullyInstalled));
        Assert.True(AppManifest.TryReadStateFlags(FullyInstalled, out var flags));
        Assert.Equal(4, flags);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("1026")]
    [InlineData("6")]
    public void OtherStateFlags_AreNotInstalled(string value)
    {
        var text = FullyInstalled.Replace("\"StateFlags\"\t\t\"4\"", $"\"StateFlags\"\t\t\"{value}\"", StringComparison.Ordinal);

        Assert.True(AppManifest.TryReadStateFlags(text, out _));
        Assert.False(AppManifest.IsFullyInstalled(text));
    }

    [Fact]
    public void MissingStateFlags_IsNotInstalled()
    {
        const string text = "\"AppState\"\n{\n\t\"appid\"\t\t\"2430930\"\n}\n";

        Assert.False(AppManifest.TryReadStateFlags(text, out _));
        Assert.False(AppManifest.IsFullyInstalled(text));
        Assert.False(AppManifest.IsFullyInstalled(string.Empty));
    }

    [Fact]
    public void BuildId_IsReadWhenPresent()
    {
        var text = FullyInstalled.Replace("\t\"installdir\"", "\t\"buildid\"\t\t\"20250901\"\n\t\"installdir\"", StringComparison.Ordinal);

        Assert.True(AppManifest.TryReadBuildId(text, out var buildId));
        Assert.Equal("20250901", buildId);
        Assert.False(AppManifest.TryReadBuildId(FullyInstalled, out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void NestedKeyWithSameName_DoesNotConfuseTheReader()
    {
        // A value that merely contains the text must not match; the key has to be on its own line.
        const string text = "\"AppState\"\n{\n\t\"note\"\t\t\"StateFlags 4 in a comment\"\n\t\"StateFlags\"\t\t\"2\"\n}\n";

        Assert.True(AppManifest.TryReadStateFlags(text, out var flags));
        Assert.Equal(2, flags);
    }
}
