using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.UnitTests.Processes;

/// <summary>The crash-loop guard (B4): consecutive short-lived automatic restarts, not a sliding window of restarts.</summary>
public class CrashLoopRuleTests
{
    private static readonly TimeSpan _quick = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    public void ShortSessions_RestartWithTheNextAttempt(int autoRestarts, int attempt)
    {
        var decision = CrashLoopRule.Next(autoRestarts, _quick);

        Assert.Equal(new CrashLoopDecision.Restart(attempt), decision);
    }

    [Fact]
    public void AfterThreeShortLivedRestarts_ItGivesUp()
    {
        Assert.Same(CrashLoopDecision.GiveUp.Instance, CrashLoopRule.Next(CrashLoopRule.MaxRestarts, _quick));
        Assert.Same(CrashLoopDecision.GiveUp.Instance, CrashLoopRule.Next(CrashLoopRule.MaxRestarts + 5, _quick));
    }

    [Fact]
    public void ASessionThatStayedUpForTheBound_StartsTheCountAgain()
    {
        Assert.Equal(new CrashLoopDecision.Restart(1), CrashLoopRule.Next(CrashLoopRule.MaxRestarts, CrashLoopRule.ShortLived));
        Assert.Equal(new CrashLoopDecision.Restart(1), CrashLoopRule.Next(2, TimeSpan.FromHours(5)));
        Assert.Same(CrashLoopDecision.GiveUp.Instance, CrashLoopRule.Next(CrashLoopRule.MaxRestarts, CrashLoopRule.ShortLived - TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// Where the rule differs from the spec's "3 restarts within 10 minutes" window on purpose: a modded server that loads
    /// for four minutes and dies every time never has three restarts inside any 10-minute window, so a window would
    /// relaunch it forever. Each of those sessions is short-lived, so the rule gives up after the third restart.
    /// </summary>
    [Fact]
    public void ShortSessionsSpreadOverMoreThanTenMinutes_StillExhaustTheBudget()
    {
        var uptime = TimeSpan.FromMinutes(4);
        var restarts = 0;
        var elapsed = uptime;
        var decision = CrashLoopRule.Next(restarts, uptime);
        while (decision is CrashLoopDecision.Restart restart)
        {
            restarts = restart.Attempt;
            elapsed += uptime;
            decision = CrashLoopRule.Next(restarts, uptime);
        }

        Assert.Equal(CrashLoopRule.MaxRestarts, restarts);
        Assert.True(elapsed > TimeSpan.FromMinutes(10), $"the loop ran for {elapsed}, inside one window");
    }

    [Fact]
    public void ANegativeCount_IsTreatedAsNone() =>
        Assert.Equal(new CrashLoopDecision.Restart(1), CrashLoopRule.Next(-1, _quick));

    [Fact]
    public void TheGiveUpMessage_NamesTheLimit() =>
        Assert.Equal(
            "The server exited unexpectedly again after 3 automatic restarts; automatic restart gave up. Start it to try again.",
            CrashLoopRule.GaveUpMessage);
}
