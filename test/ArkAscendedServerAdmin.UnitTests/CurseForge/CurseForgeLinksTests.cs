using ArkAscendedServerAdmin.CurseForge;

namespace ArkAscendedServerAdmin.UnitTests.CurseForge;

public class CurseForgeLinksTests
{
    [Theory]
    [InlineData("https://www.curseforge.com/ark-survival-ascended/mods/awesome-spyglass")]
    [InlineData("https://curseforge.com/ark-survival-ascended/mods/awesome-spyglass")]
    [InlineData("https://legacy.curseforge.com/ark-survival-ascended/mods/awesome-spyglass")]
    [InlineData("https://WWW.CurseForge.com/ark-survival-ascended/mods/awesome-spyglass")]
    public void SafeWebsiteUrl_KeepsHttpsLinksOnCurseForge(string url)
    {
        var safe = CurseForgeLinks.SafeWebsiteUrl(url);

        Assert.NotNull(safe);
        Assert.StartsWith("https://", safe);
        Assert.EndsWith("/ark-survival-ascended/mods/awesome-spyglass", safe);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://www.curseforge.com/ark-survival-ascended/mods/x")] // not https
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("/ark-survival-ascended/mods/x")] // relative
    [InlineData("//www.curseforge.com/ark-survival-ascended/mods/x")] // scheme-relative
    [InlineData("https://evil.example/ark-survival-ascended/mods/x")]
    [InlineData("https://notcurseforge.com/mods/x")] // suffix without the dot
    [InlineData("https://curseforge.com.evil.example/mods/x")]
    [InlineData("https://user:pass@www.curseforge.com/mods/x")] // credentials in the authority
    [InlineData("ftp://www.curseforge.com/mods/x")]
    [InlineData("not a url")]
    public void SafeWebsiteUrl_RejectsAnythingElse(string? url) =>
        Assert.Null(CurseForgeLinks.SafeWebsiteUrl(url));

    [Fact]
    public void SafeWebsiteUrl_RejectsAValueLongerThanTheColumn()
    {
        var url = "https://www.curseforge.com/" + new string('a', CurseForgeLinks.MaxLength);

        Assert.Null(CurseForgeLinks.SafeWebsiteUrl(url));
    }
}
