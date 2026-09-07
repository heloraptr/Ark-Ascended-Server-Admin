using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.UnitTests.Processes;

public class ProcessMatcherTests
{
    private const string InstancesRoot = @"D:\Ark\Instances";
    private static readonly DateTimeOffset _started = new(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExactToken_MatchesOnlyItsOwnSlug()
    {
        var alpha = Candidate(100, "alpha");
        var alpha2 = Candidate(200, "alpha2");

        var match = ProcessMatcher.Match(Request("alpha"), [alpha2, alpha]);

        var attach = Assert.IsType<ProcessMatch.Attach>(match);
        Assert.Same(alpha, attach.Process);
    }

    [Fact]
    public void ExecutableOutsideInstancesRoot_IsIgnored()
    {
        var foreign = Candidate(100, "alpha", executable: @"D:\Elsewhere\ShooterGame\Binaries\Win64\ArkAscendedServer.exe");
        var sibling = Candidate(101, "alpha", executable: @"D:\Ark\InstancesOld\alpha\ShooterGame\Binaries\Win64\ArkAscendedServer.exe");
        var nullPath = Candidate(102, "alpha", executable: null);

        var match = ProcessMatcher.Match(Request("alpha"), [foreign, sibling, nullPath]);

        Assert.IsType<ProcessMatch.NotRunning>(match);
    }

    [Fact]
    public void PidAndStartTimeWithinTolerance_WinOverAnotherTokenMatch()
    {
        var byIdentity = Candidate(100, "alpha", creation: _started + TimeSpan.FromSeconds(1.5));
        var byToken = Candidate(200, "alpha");

        var match = ProcessMatcher.Match(Request("alpha", lastPid: 100, lastStart: _started), [byToken, byIdentity]);

        var attach = Assert.IsType<ProcessMatch.Attach>(match);
        Assert.Same(byIdentity, attach.Process);
    }

    [Fact]
    public void StalePid_FallsBackToTokenMatch()
    {
        var reusedPid = Candidate(100, "alpha", creation: _started + TimeSpan.FromHours(1));
        var byToken = Candidate(200, "alpha");

        var match = ProcessMatcher.Match(Request("alpha", lastPid: 100, lastStart: _started), [reusedPid, byToken]);

        // Both pass the token filter, so identity failing leaves two candidates.
        var ambiguous = Assert.IsType<ProcessMatch.Ambiguous>(match);
        Assert.Equal(2, ambiguous.Candidates.Count);
    }

    [Fact]
    public void StalePidOnForeignProcess_FallsBackToTheSingleTokenMatch()
    {
        var reusedPid = Candidate(100, "beta", creation: _started + TimeSpan.FromHours(1));
        var byToken = Candidate(200, "alpha");

        var match = ProcessMatcher.Match(Request("alpha", lastPid: 100, lastStart: _started), [reusedPid, byToken]);

        var attach = Assert.IsType<ProcessMatch.Attach>(match);
        Assert.Same(byToken, attach.Process);
    }

    [Fact]
    public void IdentityMatchMustPassTheTokenFilter()
    {
        var wrongSlug = Candidate(100, "beta", creation: _started);

        var match = ProcessMatcher.Match(Request("alpha", lastPid: 100, lastStart: _started), [wrongSlug]);

        Assert.IsType<ProcessMatch.NotRunning>(match);
    }

    [Fact]
    public void NoCandidates_IsNotRunning()
    {
        Assert.IsType<ProcessMatch.NotRunning>(ProcessMatcher.Match(Request("alpha"), []));
    }

    [Fact]
    public void TwoTokenMatches_AreAmbiguous()
    {
        var first = Candidate(100, "alpha");
        var second = Candidate(200, "alpha");

        var match = ProcessMatcher.Match(Request("alpha"), [first, second]);

        var ambiguous = Assert.IsType<ProcessMatch.Ambiguous>(match);
        Assert.Equal([first, second], ambiguous.Candidates);
    }

    [Fact]
    public void RootPrefix_IsCaseInsensitiveAndAcceptsEitherSeparator()
    {
        var upper = Candidate(100, "alpha", executable: @"d:\ark\instances\alpha\ShooterGame\Binaries\Win64\ArkAscendedServer.exe");
        var forward = Candidate(200, "beta", executable: "D:/Ark/Instances/beta/ShooterGame/Binaries/Win64/ArkAscendedServer.exe");

        Assert.IsType<ProcessMatch.Attach>(ProcessMatcher.Match(Request("alpha"), [upper]));
        Assert.IsType<ProcessMatch.Attach>(ProcessMatcher.Match(Request("beta", root: @"D:\Ark\Instances\"), [forward]));
    }

    private static ProcessMatchRequest Request(string slug, int? lastPid = null, DateTimeOffset? lastStart = null, string root = InstancesRoot) =>
        new(slug, root, lastPid, lastStart);

    private static GameProcessInfo Candidate(int pid, string slug, string? executable = "", DateTimeOffset? creation = null)
    {
        var path = executable == string.Empty
            ? $@"{InstancesRoot}\{slug}\ShooterGame\Binaries\Win64\ArkAscendedServer.exe"
            : executable;
        var commandLine = $"\"{path ?? "ArkAscendedServer.exe"}\" TheIsland_WP?listen?AltSaveDirectoryName={slug} -port=7777 -log";
        return new GameProcessInfo(pid, path, commandLine, creation ?? _started + TimeSpan.FromMinutes(pid));
    }
}
