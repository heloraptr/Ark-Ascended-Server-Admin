using ArkAscendedServerAdmin.Ini;

namespace ArkAscendedServerAdmin.UnitTests.Ini;

public sealed class WhitelistUnionTests
{
    [Fact]
    public void Union_KeepsTheOrderGiven_SkipsBlanksAndComments_AndDeduplicates()
    {
        var union = IniGenerator.UnionWhitelist("m1\r\nshared", "shared\n; comment\nc1", "i1\r\n\r\nm1");

        Assert.Equal(["m1", "shared", "c1", "i1"], union);
    }

    [Fact]
    public void Generate_ListsTheManagerIdsFirst()
    {
        var result = IniGenerator.Generate(new GenerationInput(
            IniTemplates.DefaultGameIni, IniTemplates.DefaultGameUserSettings, "Fresh", 7777, 27020, 20, [], "c1", "i1", "m1\r\nc1"));

        Assert.Equal(["m1", "c1", "i1"], result.AdminWhitelist);
    }

    [Fact]
    public void Generate_WithoutAManagerList_IsUnchanged()
    {
        var result = IniGenerator.Generate(new GenerationInput(
            IniTemplates.DefaultGameIni, IniTemplates.DefaultGameUserSettings, "Fresh", 7777, 27020, 20, [], "c1", "i1"));

        Assert.Equal(["c1", "i1"], result.AdminWhitelist);
    }
}
