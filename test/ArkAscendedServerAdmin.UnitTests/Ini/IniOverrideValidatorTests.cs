using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Ini;

namespace ArkAscendedServerAdmin.UnitTests.Ini;

public class IniOverrideValidatorTests
{
    [Fact]
    public void ValidOverride_HasNoProblems()
    {
        Assert.Empty(IniOverrideValidator.Validate("/Script/ShooterGame.ShooterGameMode", "TamingSpeedMultiplier", "2"));
        Assert.Empty(IniOverrideValidator.Validate("ServerSettings", "+ActiveMods", "123,456"));
        Assert.Empty(IniOverrideValidator.Validate("ServerSettings", "Key", "a=b (c) \"d\""));
        Assert.Empty(IniOverrideValidator.Validate("ServerSettings", "Key", null));
    }

    [Theory]
    [InlineData(null, "Key", "Section is required.")]
    [InlineData("  ", "Key", "Section is required.")]
    [InlineData("[Section]", "Key", "Section must not contain '[' or ']'.")]
    [InlineData("Sec\ntion", "Key", "Section must not contain line breaks.")]
    public void BadSection_IsReported(string? section, string key, string expected)
    {
        Assert.Contains(expected, IniOverrideValidator.Validate(section, key, "v"));
    }

    [Theory]
    [InlineData(null, "Key is required.")]
    [InlineData("   ", "Key is required.")]
    [InlineData("Key=1", "Key must not contain '='.")]
    [InlineData("Ke[y]", "Key must not contain '[' or ']'.")]
    [InlineData("Ke\ry", "Key must not contain line breaks.")]
    [InlineData("+", "Key must contain more than an array prefix.")]
    public void BadKey_IsReported(string? key, string expected)
    {
        Assert.Contains(expected, IniOverrideValidator.Validate("S", key, "v"));
    }

    [Fact]
    public void ValueWithLineBreak_IsReported()
    {
        Assert.Contains("Value must not contain line breaks.", IniOverrideValidator.Validate("S", "K", "a\nb"));
    }

    [Theory]
    [InlineData("Port")]
    [InlineData("sessionname")]
    [InlineData("+RCONPort")]
    [InlineData(" !MaxPlayers ")]
    public void ReservedKey_IsRejected_NamingKeyAndManager(string key)
    {
        var problems = IniOverrideValidator.Validate("S", key, "v");

        var problem = Assert.Single(problems);
        Assert.Contains($"'{key.Trim()}'", problem, StringComparison.Ordinal);
        Assert.Contains("manager", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerAdminPassword_IsNotReserved()
    {
        Assert.Empty(IniOverrideValidator.Validate("ServerSettings", "ServerAdminPassword", "hunter2"));
    }

    [Fact]
    public void MultipleProblems_AreAllReported()
    {
        var problems = IniOverrideValidator.Validate("[S]", "Port=1", "a\nb");

        Assert.Equal(
            [
                "Section must not contain '[' or ']'.",
                "Key must not contain '='.",
                "Value must not contain line breaks.",
            ],
            problems);
    }

    [Fact]
    public void IniOverrideSpec_From_CopiesDomainFields()
    {
        var o = new ExtraOverride { File = IniFile.Game, Section = "S", Key = "K", Value = "V" };

        Assert.Equal(new IniOverrideSpec(IniFile.Game, "S", "K", "V"), IniOverrideSpec.From(o));
    }
}
