using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ArkAscendedServerAdmin.Firewall;

namespace ArkAscendedServerAdmin.UnitTests.Firewall;

public partial class FirewallRuleNamesTests
{
    // Built from the temp path so the roots are rooted on any OS; nothing is created on disk.
    private static readonly string _root = Path.Combine(Path.GetTempPath(), "ArkRoot");

    [Fact]
    public void InstallTag_IsTheSameForTheSameRootSpelledDifferently()
    {
        var tag = FirewallRuleNames.InstallTag(_root);

        Assert.Equal(tag, FirewallRuleNames.InstallTag(_root.ToUpperInvariant()));
        Assert.Equal(tag, FirewallRuleNames.InstallTag(_root.ToLowerInvariant()));
        Assert.Equal(tag, FirewallRuleNames.InstallTag(_root + Path.DirectorySeparatorChar));
        Assert.Equal(tag, FirewallRuleNames.InstallTag(_root.ToLowerInvariant() + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void InstallTag_DiffersBetweenRoots()
    {
        Assert.NotEqual(FirewallRuleNames.InstallTag(_root), FirewallRuleNames.InstallTag(_root + "2"));
        Assert.NotEqual(FirewallRuleNames.InstallTag(_root), FirewallRuleNames.InstallTag(Path.Combine(_root, "Dev")));
    }

    [Fact]
    public void InstallTag_IsEightLowercaseHexCharacters_OfTheSha256OfTheUpperCasedFullPath()
    {
        var tag = FirewallRuleNames.InstallTag(_root);

        Assert.Matches(TagPattern(), tag);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_root).ToUpperInvariant())))[..8];
        Assert.Equal(expected, tag);
    }

    [Fact]
    public void RuleName_IsPrefixTagAndInstanceId()
    {
        Assert.Equal("ArkAscendedServerAdmin-0123abcd-42", FirewallRuleNames.RuleName("0123abcd", 42));
        Assert.StartsWith(FirewallRuleNames.NamePrefix, FirewallRuleNames.RuleName("0123abcd", 42), StringComparison.Ordinal);
    }

    [Fact]
    public void Description_NamesThePortTheInstanceAndTheRoot() =>
        Assert.Equal(
            @"ArkAscendedServerAdmin: UDP 7777 for instance 3 (C:\Ark)",
            FirewallRuleNames.Description(3, 7777, @"C:\Ark"));

    [GeneratedRegex("^[0-9a-f]{8}$")]
    private static partial Regex TagPattern();
}
