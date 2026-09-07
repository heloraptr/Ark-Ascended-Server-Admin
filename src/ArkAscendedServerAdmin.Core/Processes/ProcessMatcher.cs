using ArkAscendedServerAdmin.Launch;

namespace ArkAscendedServerAdmin.Processes;

/// <summary>What the reconciler knows about one instance before it looks at the process table (plan step 21).</summary>
/// <param name="Slug">The instance slug; must equal the <c>AltSaveDirectoryName</c> map token exactly.</param>
/// <param name="InstancesRoot">The <c>DataRoot\Instances</c> directory; only executables under it are managed.</param>
/// <param name="LastPid">The persisted pid from the last launch, when known.</param>
/// <param name="LastProcessStartTime">The persisted <c>Process.StartTime</c> from the last launch, when known.</param>
public sealed record ProcessMatchRequest(string Slug, string InstancesRoot, int? LastPid, DateTimeOffset? LastProcessStartTime);

/// <summary>The reconciler's verdict for one instance: attach to a process, nothing running, or ambiguous.</summary>
public abstract record ProcessMatch
{
    private ProcessMatch()
    {
    }

    /// <summary>Exactly one process belongs to the instance.</summary>
    public sealed record Attach(GameProcessInfo Process) : ProcessMatch;

    /// <summary>No process belongs to the instance; it is Stopped.</summary>
    public sealed record NotRunning : ProcessMatch
    {
        public static readonly NotRunning Instance = new();
    }

    /// <summary>More than one process claims the instance; the caller marks it Unknown and takes no action.</summary>
    public sealed record Ambiguous(IReadOnlyList<GameProcessInfo> Candidates) : ProcessMatch;
}

/// <summary>
/// The pure reconciliation matcher (plan step 21). A candidate is <i>managed</i> when its executable lies
/// under <see cref="ProcessMatchRequest.InstancesRoot"/> (WMI reports the junction path, Spike B) and its
/// command line carries the exact <c>AltSaveDirectoryName=&lt;slug&gt;</c> token. Identity by pid + start time
/// wins when it passes that filter; otherwise the token match alone decides.
/// </summary>
public static class ProcessMatcher
{
    /// <summary>How far WMI <c>CreationDate</c> may drift from the persisted start time (observed: identical to the millisecond).</summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    public static ProcessMatch Match(ProcessMatchRequest request, IReadOnlyList<GameProcessInfo> candidates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstancesRoot);

        var managed = candidates.Where(candidate => IsManaged(candidate, request)).ToList();

        if (request.LastPid is { } pid && request.LastProcessStartTime is { } startTime)
        {
            var byIdentity = managed.FirstOrDefault(candidate =>
                candidate.Pid == pid && (candidate.CreationTime - startTime).Duration() <= StartTimeTolerance);
            if (byIdentity is not null)
            {
                return new ProcessMatch.Attach(byIdentity);
            }
        }

        return managed.Count switch
        {
            0 => ProcessMatch.NotRunning.Instance,
            1 => new ProcessMatch.Attach(managed[0]),
            _ => new ProcessMatch.Ambiguous(managed),
        };
    }

    /// <summary>True when the executable is under the instances root and the command line names the slug exactly.</summary>
    public static bool IsManaged(GameProcessInfo candidate, ProcessMatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(request);

        return candidate.ExecutablePath is not null
            && candidate.CommandLine is not null
            && IsUnder(candidate.ExecutablePath, request.InstancesRoot)
            && LaunchCommandLine.MatchesSlug(candidate.CommandLine, request.Slug);
    }

    /// <summary>Ordinal-ignore-case prefix test that accepts either separator so the pure test suite runs off Windows too.</summary>
    private static bool IsUnder(string path, string root)
    {
        var normalizedPath = path.Replace('/', '\\');
        var normalizedRoot = root.Replace('/', '\\').TrimEnd('\\');
        if (normalizedRoot.Length == 0 || normalizedPath.Length <= normalizedRoot.Length)
        {
            return false;
        }

        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && normalizedPath[normalizedRoot.Length] == '\\';
    }
}
