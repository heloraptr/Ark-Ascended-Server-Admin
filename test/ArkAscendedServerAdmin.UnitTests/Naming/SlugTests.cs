using ArkAscendedServerAdmin.Naming;

namespace ArkAscendedServerAdmin.UnitTests.Naming;

public class SlugTests
{
    [Theory]
    [InlineData("Thé Island #2!", "the-island-2")]
    [InlineData("The Island", "the-island")]
    [InlineData("  Scorched   Earth  ", "scorched-earth")]
    [InlineData("Ragnarök", "ragnarok")]
    [InlineData("Ａｂｅｒｒａｔｉｏｎ", "aberration")]
    [InlineData("Straße", "stra-e")]
    [InlineData("--island--", "island")]
    [InlineData("island?=x\r\ny", "island-x-y")]
    [InlineData("42", "42")]
    public void Generate_NormalizesNames(string name, string expected)
    {
        var slug = Slug.Generate(name, []);

        Assert.Equal(expected, slug);
        Assert.True(Slug.IsValid(slug));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ### ???")]
    [InlineData("日本語")]
    public void Generate_EmptyOrOnlySymbols_FallsBackToInstance(string name)
    {
        Assert.Equal(Slug.Fallback, Slug.Generate(name, []));
        Assert.Equal("instance", Slug.Fallback);
    }

    [Fact]
    public void Generate_TruncatesToMaxLength()
    {
        var slug = Slug.Generate(new string('a', 40), []);

        Assert.Equal(new string('a', Slug.MaxLength), slug);
        Assert.True(Slug.IsValid(slug));
    }

    [Fact]
    public void Generate_TruncationDoesNotLeaveTrailingHyphen()
    {
        // 31 letters, a space, then more: the cut lands right after the hyphen.
        var slug = Slug.Generate(new string('a', 31) + " bbbbb", []);

        Assert.Equal(new string('a', 31), slug);
    }

    [Fact]
    public void Generate_AppendsNumericSuffix_WhenReserved()
    {
        Assert.Equal("island-2", Slug.Generate("Island", ["island"]));
        Assert.Equal("island-3", Slug.Generate("Island", ["island", "island-2"]));
        Assert.Equal("island-2", Slug.Generate("Island", ["ISLAND"]));
        Assert.Equal("island-2-2", Slug.Generate("Island 2", ["island-2"]));
    }

    [Fact]
    public void Generate_ShortensBaseToFitSuffix()
    {
        var full = new string('a', Slug.MaxLength);
        var reserved = new List<string> { full };
        var slug = Slug.Generate(full, reserved);

        Assert.Equal(new string('a', Slug.MaxLength - 2) + "-2", slug);
        Assert.Equal(Slug.MaxLength, slug.Length);

        for (var i = 2; i < 12; i++)
        {
            reserved.Add(slug);
            slug = Slug.Generate(full, reserved);
        }

        Assert.Equal(new string('a', Slug.MaxLength - 3) + "-12", slug);
        Assert.Equal(Slug.MaxLength, slug.Length);
    }

    [Fact]
    public void Generate_ShortenedBaseDoesNotEndWithHyphen()
    {
        // 29 letters, a hyphen, two more letters: cutting to 30 for "-2" lands right after the hyphen and
        // would otherwise leave "aaa…-" + "-2".
        var name = new string('a', 29) + " bb";
        var reserved = new[] { new string('a', 29) + "-bb" };

        Assert.Equal(new string('a', 29) + "-2", Slug.Generate(name, reserved));
    }

    [Fact]
    public void Generate_TreatsParsedArchiveNamesAsReserved()
    {
        var reserved = new[] { Slug.TryParseArchiveDirectoryName("island-20260907-101500")! };

        Assert.Equal("island-2", Slug.Generate("Island", reserved));
    }

    [Theory]
    [InlineData("CON", "con-2")]
    [InlineData("nul", "nul-2")]
    [InlineData("COM1", "com1-2")]
    [InlineData("lpt9", "lpt9-2")]
    [InlineData("aux", "aux-2")]
    [InlineData("prn", "prn-2")]
    public void Generate_AvoidsWindowsDeviceNames(string name, string expected)
    {
        Assert.Equal(expected, Slug.Generate(name, []));
    }

    [Fact]
    public void Generate_AvoidsNamesThatLookLikeArchiveEntries()
    {
        var slug = Slug.Generate("island 20260907 101500", []);

        Assert.Equal("island-20260907-101500-2", slug);
        Assert.True(Slug.IsValid(slug));
    }

    [Fact]
    public void Generate_IgnoresBlankReservedEntries()
    {
        Assert.Equal("island", Slug.Generate("Island", ["", "  "]));
    }

    [Fact]
    public void Generate_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => Slug.Generate(null!, []));
        Assert.Throws<ArgumentNullException>(() => Slug.Generate("Island", null!));
    }

    [Theory]
    [InlineData("island-20260907-101500", "island")]
    [InlineData("the-island-2-20260101-000000", "the-island-2")]
    [InlineData("a-20261231-235959", "a")]
    public void TryParseArchiveDirectoryName_ReturnsSlug(string directoryName, string expected)
    {
        Assert.Equal(expected, Slug.TryParseArchiveDirectoryName(directoryName));
    }

    [Theory]
    [InlineData("island")]
    [InlineData("island-20261399-000000")]
    [InlineData("island-20260907-250000")]
    [InlineData("island-2026090-101500")]
    [InlineData("island-20260907-10150")]
    [InlineData("island-20260907101500")]
    [InlineData("island_20260907-101500")]
    [InlineData("island-2026O907-101500")]
    [InlineData("-20260907-101500")]
    [InlineData("20260907-101500")]
    [InlineData("Island-20260907-101500")]
    [InlineData("con-20260907-101500")]
    [InlineData("island--20260907-101500")]
    [InlineData("")]
    public void TryParseArchiveDirectoryName_ReturnsNull(string directoryName)
    {
        Assert.Null(Slug.TryParseArchiveDirectoryName(directoryName));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("7")]
    [InlineData("ab")]
    [InlineData("a-b")]
    [InlineData("the-island-2")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaa-aaaaaaaaaaaaaaaa")]
    [InlineData("con-2")]
    [InlineData("com10")]
    public void IsValid_AcceptsValidSlugs(string slug)
    {
        Assert.True(Slug.IsValid(slug));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("-a")]
    [InlineData("a-")]
    [InlineData("A")]
    [InlineData("a b")]
    [InlineData("a_b")]
    [InlineData("a.b")]
    [InlineData("a?b")]
    [InlineData("a=b")]
    [InlineData("é")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("con")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("com1")]
    [InlineData("lpt1")]
    [InlineData("island-20260907-101500")]
    public void IsValid_RejectsInvalidSlugs(string? slug)
    {
        Assert.False(Slug.IsValid(slug));
    }

    [Fact]
    public void IsValid_LengthBoundaries()
    {
        Assert.True(Slug.IsValid(new string('a', Slug.MaxLength)));
        Assert.False(Slug.IsValid(new string('a', Slug.MaxLength + 1)));
    }
}
