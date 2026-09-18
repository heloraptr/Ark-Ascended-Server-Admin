using ArkAscendedServerAdmin.Launch;

namespace ArkAscendedServerAdmin.UnitTests.Launch;

public class AdditionalArgsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t ")]
    public void Tokenize_BlankText_YieldsNoTokens(string? text)
    {
        Assert.Empty(AdditionalArgs.Tokenize(text));
    }

    [Fact]
    public void Tokenize_SplitsOnAnyWhitespace()
    {
        var tokens = AdditionalArgs.Tokenize("  -culture=en\t-crossplay \n -foo ");

        Assert.Equal(["-culture=en", "-crossplay", "-foo"], tokens);
    }

    [Fact]
    public void Tokenize_QuotesGroupAndAreStripped()
    {
        var tokens = AdditionalArgs.Tokenize("-foo=\"a b\" \"-bar=c d\" -baz");

        Assert.Equal(["-foo=a b", "-bar=c d", "-baz"], tokens);
    }

    [Fact]
    public void Tokenize_EmptyQuotes_YieldEmptyToken()
    {
        Assert.Equal([""], AdditionalArgs.Tokenize("\"\""));
    }

    [Fact]
    public void Tokenize_UnbalancedQuote_Throws()
    {
        Assert.Throws<FormatException>(() => AdditionalArgs.Tokenize("-foo=\"a b"));
    }

    [Fact]
    public void Validate_AcceptsOrdinaryArguments()
    {
        Assert.Empty(AdditionalArgs.Validate("-culture=en -crossplay -foo=\"a b\""));
        Assert.Empty(AdditionalArgs.Validate(null));
    }

    [Fact]
    public void Validate_UnbalancedQuote_IsReported()
    {
        var problems = AdditionalArgs.Validate("-foo=\"a b");

        var problem = Assert.Single(problems);
        Assert.Contains("unbalanced", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-port=1")]
    [InlineData("-PORT=7777")]
    [InlineData("-mods=x")]
    [InlineData("-clusterid=main")]
    [InlineData("-ClusterDirOverride=D:\\x")]
    [InlineData("-WinLiveMaxPlayers=10")]
    [InlineData("-log")]
    public void Validate_ManagerOwnedOption_IsRejectedNamingTheToken(string token)
    {
        var problems = AdditionalArgs.Validate($"-culture=en {token}");

        var problem = Assert.Single(problems);
        Assert.Contains($"'{token}'", problem);
        Assert.Contains("managed by the manager", problem);
    }

    [Theory]
    [InlineData("-NoBattlEye")]
    [InlineData("-nobattleye")]
    [InlineData("-ServerPlatform=ALL")]
    [InlineData("-exclusivejoin")]
    [InlineData("-NoWildBabies")]
    [InlineData("-UseStore")]
    [InlineData("-ConvertToStore")]
    [InlineData("-servergamelog")]
    [InlineData("-ServerGameLogIncludeTribeLogs")]
    [InlineData("-ServerRconOutputTribeLogs")]
    [InlineData("-ActiveEvent=Summer")]
    public void Validate_TypedFlag_IsRejectedPointingAtTypedOption(string token)
    {
        var problems = AdditionalArgs.Validate(token);

        var problem = Assert.Single(problems);
        Assert.Contains($"'{token}'", problem);
        Assert.Contains("typed option", problem);
    }

    [Theory]
    [InlineData("?SessionName=x")]
    [InlineData("-foo=a?b")]
    [InlineData("TheIsland_WP?listen")]
    public void Validate_MapStringDelimiter_IsRejected(string token)
    {
        var problems = AdditionalArgs.Validate(token);

        var problem = Assert.Single(problems);
        Assert.Contains($"'{token}'", problem);
        Assert.Contains("INI", problem);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("\"\"")]
    public void Validate_DegenerateToken_IsRejected(string text)
    {
        var problems = AdditionalArgs.Validate(text);

        var problem = Assert.Single(problems);
        Assert.Contains("not a valid argument", problem);
    }

    [Fact]
    public void Validate_ReportsEveryProblem()
    {
        var problems = AdditionalArgs.Validate("-port=1 -ok -NoBattlEye a?b");

        Assert.Equal(3, problems.Count);
    }

    [Fact]
    public void TypedFlagOptions_AreAllReserved()
    {
        foreach (var option in AdditionalArgs.TypedFlagOptions)
        {
            Assert.Contains(option, ReservedKeys.CommandLineOptions);
        }
    }
}
