using ArkAscendedServerAdmin.Install;

namespace ArkAscendedServerAdmin.UnitTests.Install;

/// <summary>Lines are verbatim from the Phase 2 spike's <c>steamcmd-install2.log</c>.</summary>
public class SteamCmdOutputTests
{
    [Theory]
    [InlineData(" Update state (0x61) downloading, progress: 0.14 (17477272 / 12206318952)", "downloading", 0.14, 17477272L, 12206318952L)]
    [InlineData(" Update state (0x61) downloading, progress: 15.67 (1912380961 / 12206318952)", "downloading", 15.67, 1912380961L, 12206318952L)]
    [InlineData(" Update state (0x3) reconfiguring, progress: 0.00 (0 / 0)", "reconfiguring", 0.0, 0L, 0L)]
    [InlineData("Update state (0x81) verifying update, progress: 99.50 (12145000000 / 12206318952)", "verifying update", 99.5, 12145000000L, 12206318952L)]
    public void ParsesProgressLines(string line, string state, double percent, long done, long total)
    {
        Assert.True(SteamCmdOutput.TryParseProgress(line, out var progress));
        Assert.Equal(state, progress.State);
        Assert.Equal(percent, progress.Percent, precision: 3);
        Assert.Equal(done, progress.Done);
        Assert.Equal(total, progress.Total);
    }

    [Theory]
    [InlineData("Steam Console Client (c) Valve Corporation - version 1788292693")]
    [InlineData("Connecting anonymously to Steam Public...OK")]
    [InlineData("Success! App '2430930' fully installed.")]
    [InlineData("[  0%] Checking for available updates...")]
    [InlineData("")]
    public void IgnoresOtherLines(string line)
    {
        Assert.False(SteamCmdOutput.TryParseProgress(line, out var progress));
        Assert.Null(progress);
    }

    [Fact]
    public void RecognizesTheSuccessLineForTheRightApp()
    {
        Assert.True(SteamCmdOutput.IsSuccess("Success! App '2430930' fully installed.", 2430930));
        Assert.False(SteamCmdOutput.IsSuccess("Success! App '2430930' fully installed.", 376030));
        Assert.False(SteamCmdOutput.IsSuccess("Error! App '2430930' state is 0x6 after update job.", 2430930));
    }

    [Fact]
    public void RecognizesTheSelfUpdateLine()
    {
        Assert.True(SteamCmdOutput.IsSelfUpdate("Update complete, launching..."));
        Assert.True(SteamCmdOutput.IsSelfUpdate("  Update complete, launching...\r"));
        Assert.False(SteamCmdOutput.IsSelfUpdate("[----] Verifying installation..."));
    }
}

public class SteamCmdRetryPolicyTests
{
    [Fact]
    public void DefaultsDoubleFrom30SecondsAcrossFiveAttempts()
    {
        var policy = new SteamCmdRetryPolicy();

        Assert.Equal(5, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.DelayAfter(1));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.DelayAfter(2));
        Assert.Equal(TimeSpan.FromSeconds(120), policy.DelayAfter(3));
        Assert.Equal(TimeSpan.FromSeconds(240), policy.DelayAfter(4));
        Assert.Null(policy.DelayAfter(5));
        Assert.Null(policy.DelayAfter(6));
    }

    [Fact]
    public void DelayIsCappedAtMaxDelay()
    {
        var policy = new SteamCmdRetryPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(100), maxAttempts: 10);

        Assert.Equal(TimeSpan.FromSeconds(60), policy.DelayAfter(2));
        Assert.Equal(TimeSpan.FromSeconds(100), policy.DelayAfter(3));
        Assert.Equal(TimeSpan.FromSeconds(100), policy.DelayAfter(9));
        Assert.Null(policy.DelayAfter(10));
    }

    [Fact]
    public void RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteamCmdRetryPolicy(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteamCmdRetryPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteamCmdRetryPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteamCmdRetryPolicy().DelayAfter(0));
    }
}
