using ArkAscendedServerAdmin.Ini;

namespace ArkAscendedServerAdmin.UnitTests.Ini;

public sealed class IniLineEndingsTests
{
    [Theory]
    [InlineData("a\r\nb\r\n", LineEndingStyle.CrLf)]
    [InlineData("a\nb\n", LineEndingStyle.Lf)]
    [InlineData("a\r\nb\nc\nd\r\n", LineEndingStyle.CrLf)] // tie
    [InlineData("a\nb\nc\r\n", LineEndingStyle.Lf)] // LF majority
    [InlineData("a\r\nb\r\nc\n", LineEndingStyle.CrLf)] // CRLF majority
    [InlineData("", LineEndingStyle.CrLf)]
    [InlineData("no break", LineEndingStyle.CrLf)]
    [InlineData("a\rb\rc", LineEndingStyle.CrLf)] // lone CRs count for neither
    public void Detect_takes_the_majority_and_falls_back_to_crlf(string text, LineEndingStyle expected)
    {
        Assert.Equal(expected, IniLineEndings.Detect(text));
    }

    [Theory]
    [InlineData("a\nb\n", LineEndingStyle.CrLf, "a\r\nb\r\n")]
    [InlineData("a\nb", LineEndingStyle.CrLf, "a\r\nb")] // no trailing break added
    [InlineData("a\r\nb\r\n", LineEndingStyle.Lf, "a\nb\n")]
    [InlineData("a\r\nb\nc", LineEndingStyle.CrLf, "a\r\nb\r\nc")]
    [InlineData("a\rb\r\n", LineEndingStyle.CrLf, "a\r\nb\r\n")] // a lone CR is a break too
    [InlineData("a\rb", LineEndingStyle.Lf, "a\nb")]
    [InlineData("", LineEndingStyle.CrLf, "")]
    [InlineData("\n\n", LineEndingStyle.CrLf, "\r\n\r\n")]
    public void Apply_rewrites_only_the_breaks(string text, LineEndingStyle style, string expected)
    {
        Assert.Equal(expected, IniLineEndings.Apply(text, style));
    }

    [Fact]
    public void Apply_is_idempotent()
    {
        const string text = "[S]\r\nA=1\r\n\r\nB=2";
        Assert.Equal(text, IniLineEndings.Apply(text, LineEndingStyle.CrLf));
        Assert.Equal(text, IniLineEndings.Apply(IniLineEndings.Apply(text, LineEndingStyle.Lf), LineEndingStyle.CrLf));
    }

    [Fact]
    public void Equivalent_ignores_the_style_but_not_the_content_or_a_trailing_break()
    {
        Assert.True(IniLineEndings.Equivalent("a\r\nb\r\n", "a\nb\n"));
        Assert.False(IniLineEndings.Equivalent("a\r\nb\r\n", "a\nb"));
        Assert.False(IniLineEndings.Equivalent("a\r\nb", "a\nc"));
    }
}
