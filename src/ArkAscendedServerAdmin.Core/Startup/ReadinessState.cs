namespace ArkAscendedServerAdmin.Startup;

/// <summary>Phases of the startup readiness pipeline (plan step 11).</summary>
public enum ReadinessPhase
{
    /// <summary>Creating the data tree, migrating and seeding the database.</summary>
    Initializing,
    /// <summary>Reconciling surviving processes and resuming persisted maintenance.</summary>
    Recovering,
    /// <summary>SteamCMD or the game install is missing and the install is running in the background.</summary>
    Installing,
    /// <summary>The install failed after its retries; the Setup page offers a retry.</summary>
    InstallFailed,
    /// <summary>A pipeline step threw; the message names it. Only a service restart clears this.</summary>
    Failed,
    Ready,
}

public sealed record ReadinessState(ReadinessPhase Phase, string Message, string? Error, DateTimeOffset ChangedAt)
{
    public bool IsReady => Phase == ReadinessPhase.Ready;

    public bool CanRetryInstall => Phase == ReadinessPhase.InstallFailed;
}

/// <summary>Observes the readiness pipeline. <see cref="Changed"/> fires on a background thread.</summary>
public interface IReadinessMonitor
{
    ReadinessState Current { get; }

    event Action<ReadinessState>? Changed;
}

/// <summary>Owner-initiated actions on the pipeline, exposed to the UI through a guarded command facade.</summary>
public interface IStartupControl
{
    /// <summary>Re-runs the install step after an <see cref="ReadinessPhase.InstallFailed"/>; no-op otherwise.</summary>
    Task RetryInstallAsync(CancellationToken cancellationToken = default);
}
