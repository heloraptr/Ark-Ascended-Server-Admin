namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Firewall;

/// <summary>
/// The uninstaller's side of the firewall tag (<c>install\ArkInstall.Common.ps1</c>), run in a child Windows
/// PowerShell. <c>Get-NetFirewallRule</c> and <c>Remove-NetFirewallRule</c> are shadowed by functions that only
/// record their calls, so nothing here touches the real firewall or needs elevation.
/// </summary>
public sealed class InstanceFirewallScriptTests : IDisposable
{
    // Functions win over cmdlets of the same name, so these stand in for the NetSecurity module.
    private const string FirewallRecorder = """
        $script:firewallCalls = New-Object System.Collections.Generic.List[string]
        function Get-NetFirewallRule { [CmdletBinding()] param([string]$DisplayName) $script:firewallCalls.Add("get $DisplayName") }
        function Remove-NetFirewallRule { [CmdletBinding()] param([Parameter(ValueFromPipeline = $true)]$InputObject) process { $script:firewallCalls.Add('remove') } }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ArkAdminTests", Guid.NewGuid().ToString("N"));

    public InstanceFirewallScriptTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ReadInstanceFirewallTag_AcceptsOnlyEightLowercaseHexCharacters()
    {
        Assert.SkipWhen(ChildPowerShell.UnavailableReason is not null, ChildPowerShell.UnavailableReason ?? "");

        var cases = new Dictionary<string, string?>
        {
            ["valid"] = "0123abcd\n",
            ["missing"] = null,
            ["empty"] = "",
            ["prefix-wildcard"] = "ArkAscendedServerAdmin-*",
            ["wildcard"] = "*",
            ["seven"] = "0123abc",
            ["uppercase"] = "0123ABCD",
        };
        foreach (var (label, content) in cases)
        {
            var directory = Directory.CreateDirectory(Path.Combine(_root, label)).FullName;
            if (content is not null)
            {
                File.WriteAllText(Path.Combine(directory, "firewall.tag"), content);
            }
        }

        var (exitCode, lines, error) = ChildPowerShell.Run($$"""
            foreach ($label in {{string.Join(", ", cases.Keys.Select(ChildPowerShell.Quote))}}) {
                $out = @(Read-InstanceFirewallTag (Join-Path {{ChildPowerShell.Quote(_root)}} $label) 3>&1)
                $warnings = @($out | Where-Object { $_ -is [System.Management.Automation.WarningRecord] })
                $values = @($out | Where-Object { $_ -isnot [System.Management.Automation.WarningRecord] -and $null -ne $_ })
                "$label|$($values -join ',')|$($warnings.Count)|$(($warnings | ForEach-Object { $_.Message }) -join ' ')"
            }
            """);

        Assert.True(exitCode == 0, error);
        var results = lines.Select(line => line.Split('|', 4)).ToDictionary(parts => parts[0]);
        Assert.Equal(["valid", "0123abcd", "0", ""], results["valid"]);
        foreach (var label in cases.Keys.Where(label => label != "valid"))
        {
            var result = results[label];
            Assert.Equal("", result[1]);
            Assert.Equal("1", result[2]);
            Assert.Contains(Path.Combine(_root, label, "firewall.tag"), result[3], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("left in place", result[3], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RemoveInstanceFirewallRules_RefusesAnythingButATag_BeforeTouchingTheFirewall()
    {
        Assert.SkipWhen(ChildPowerShell.UnavailableReason is not null, ChildPowerShell.UnavailableReason ?? "");

        var (exitCode, lines, error) = ChildPowerShell.Run($$"""
            {{FirewallRecorder}}
            foreach ($tag in '*', '', 'ArkAscendedServerAdmin-*', '0123abc', '0123abcd*') {
                $threw = $false
                try { Remove-InstanceFirewallRules $tag } catch { $threw = $true }
                "[$tag]|$threw|$($script:firewallCalls.Count)"
            }
            """);

        Assert.True(exitCode == 0, error);
        Assert.Equal(
            ["[*]|True|0", "[]|True|0", "[ArkAscendedServerAdmin-*]|True|0", "[0123abc]|True|0", "[0123abcd*]|True|0"],
            lines);
    }

    [Fact]
    public void RemoveInstanceFirewallRules_SelectsByDisplayNameWithTheTag()
    {
        Assert.SkipWhen(ChildPowerShell.UnavailableReason is not null, ChildPowerShell.UnavailableReason ?? "");

        var (exitCode, lines, error) = ChildPowerShell.Run($$"""
            {{FirewallRecorder}}
            Remove-InstanceFirewallRules '0123abcd' | Out-Null
            $script:firewallCalls
            """);

        Assert.True(exitCode == 0, error);
        Assert.Contains("get ArkAscendedServerAdmin-0123abcd-*", lines);
    }

    [Fact]
    public void CopyInstanceFirewallTag_CopiesAValidTag_AndNothingElse()
    {
        Assert.SkipWhen(ChildPowerShell.UnavailableReason is not null, ChildPowerShell.UnavailableReason ?? "");

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        File.WriteAllText(Path.Combine(Folder("old-valid"), "firewall.tag"), "0123abcd\r\n");
        File.WriteAllText(Path.Combine(Folder("old-invalid"), "firewall.tag"), "*");
        Folder("old-missing");
        foreach (var name in new[] { "new-valid", "new-invalid", "new-missing" })
        {
            Folder(name);
        }

        var root = ChildPowerShell.Quote(_root);
        var (exitCode, lines, error) = ChildPowerShell.Run($$"""
            foreach ($name in 'valid', 'invalid', 'missing') {
                $out = @(Copy-InstanceFirewallTag (Join-Path {{root}} "old-$name") (Join-Path {{root}} "new-$name") 3>&1)
                "$name|$($out.Count)"
            }
            """);

        Assert.True(exitCode == 0, error);
        Assert.Equal(["valid|0", "invalid|1", "missing|0"], lines);
        Assert.Equal("0123abcd\n", File.ReadAllText(Path.Combine(_root, "new-valid", "firewall.tag")));
        Assert.False(File.Exists(Path.Combine(_root, "new-invalid", "firewall.tag")));
        Assert.False(File.Exists(Path.Combine(_root, "new-missing", "firewall.tag")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleaner takes the rest.
        }
    }
}
