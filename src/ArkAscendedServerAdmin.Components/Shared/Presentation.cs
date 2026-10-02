using System.Globalization;
using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Startup;

namespace ArkAscendedServerAdmin.Components.Shared;

/// <summary>State → label / tone mapping shared by the lamp, the rows, and the detail header. One color per meaning.</summary>
public static class Presentation
{
    /// <summary>CSS tone class: <c>st-running</c>, <c>st-transit</c>, <c>st-off</c>, <c>st-bad</c>.</summary>
    public static string Tone(InstanceState state) => state switch
    {
        InstanceState.Running => "st-running",
        InstanceState.Starting or InstanceState.Stopping or InstanceState.StartingUnconfirmed => "st-transit",
        InstanceState.Stopped => "st-off",
        InstanceState.Crashed => "st-bad",
        _ => "st-bad",
    };

    public static bool IsHollow(InstanceState state) => state == InstanceState.Stopped;

    public static string Label(InstanceState state) => state switch
    {
        InstanceState.Stopped => "Stopped",
        InstanceState.Starting => "Starting",
        InstanceState.StartingUnconfirmed => "Starting, unconfirmed",
        InstanceState.Running => "Running",
        InstanceState.Unreachable => "Unreachable",
        InstanceState.Stopping => "Stopping",
        InstanceState.Unknown => "Unknown",
        InstanceState.IdentityUnpersisted => "Identity not saved",
        InstanceState.Crashed => "Crashed",
        _ => state.ToString(),
    };

    /// <summary>The short explanation shown under the state where the state itself does not say enough.</summary>
    public static string? Hint(InstanceRuntime runtime) => runtime.State switch
    {
        InstanceState.StartingUnconfirmed => "Alive for over 10 minutes without answering RCON.",
        InstanceState.Unreachable => runtime.Detail ?? "Alive, but RCON keeps failing. Check ServerAdminPassword and RCONPort.",
        InstanceState.Unknown => runtime.Detail ?? "More than one process matched; nothing is done automatically.",
        InstanceState.IdentityUnpersisted => runtime.Detail ?? "The process runs but its PID could not be saved.",
        InstanceState.Crashed => runtime.Detail ?? "Automatic restart stopped trying. Start it to try again.",
        InstanceState.Starting => runtime.LastMarker switch
        {
            Consoles.StartupMarker.WorldLoaded => "World loaded, waiting to advertise.",
            Consoles.StartupMarker.Advertising => "Advertising, waiting for RCON.",
            _ => "Loading the world.",
        },
        InstanceState.Stopping => runtime.ExitRequested ? "Exit requested, waiting for the process to close." : null,
        _ => null,
    };

    public static string Tone(ReadinessPhase phase) => phase switch
    {
        ReadinessPhase.Ready => "st-running",
        ReadinessPhase.InstallFailed or ReadinessPhase.Failed => "st-bad",
        _ => "st-transit",
    };

    public static string Label(ReadinessPhase phase) => phase switch
    {
        ReadinessPhase.Initializing => "Initializing",
        ReadinessPhase.Recovering => "Recovering",
        ReadinessPhase.Installing => "Installing the game",
        ReadinessPhase.InstallFailed => "Install failed",
        ReadinessPhase.Failed => "Startup failed",
        ReadinessPhase.Ready => "Ready",
        _ => phase.ToString(),
    };

    public static string Label(MaintenancePhase phase) => phase switch
    {
        MaintenancePhase.None => "No maintenance running",
        MaintenancePhase.Installing => "Installing",
        MaintenancePhase.Stopping => "Stopping instances for update",
        MaintenancePhase.Updating => "Updating the game",
        MaintenancePhase.Restarting => "Restarting instances",
        _ => phase.ToString(),
    };

    public static string Label(BackupOutcome outcome) => outcome switch
    {
        BackupOutcome.Success => "Backed up",
        BackupOutcome.Skipped => "Skipped",
        BackupOutcome.Failed => "Failed",
        _ => outcome.ToString(),
    };

    public static string Tone(BackupOutcome outcome) => outcome switch
    {
        BackupOutcome.Success => "tone-ok",
        BackupOutcome.Skipped => "tone-warn",
        _ => "tone-bad",
    };

    public static string Label(RestoreOutcome outcome) => outcome switch
    {
        RestoreOutcome.Success => "Restored",
        RestoreOutcome.RolledBack => "Rolled back",
        RestoreOutcome.Failed => "Failed",
        _ => outcome.ToString(),
    };

    public static string Tone(RestoreOutcome outcome) => outcome switch
    {
        RestoreOutcome.Success => "tone-ok",
        RestoreOutcome.RolledBack => "tone-warn",
        _ => "tone-bad",
    };

    /// <summary>The plain name of a scheduled action kind: "Restart", "RCON command", "Wipe wild dinos".</summary>
    public static string Label(ScheduledActionKind kind) => kind switch
    {
        ScheduledActionKind.Restart => "Restart",
        ScheduledActionKind.RconCommand => "RCON command",
        ScheduledActionKind.DinoWipe => "Wipe wild dinos",
        _ => kind.ToString(),
    };

    public static string Label(ScheduledActionOutcome outcome) => outcome switch
    {
        ScheduledActionOutcome.Started => "In progress",
        ScheduledActionOutcome.Succeeded => "Done",
        ScheduledActionOutcome.Failed => "Failed",
        ScheduledActionOutcome.Skipped => "Skipped",
        ScheduledActionOutcome.Interrupted => "Interrupted",
        _ => outcome.ToString(),
    };

    /// <summary>Skipped is neutral (the day passed without the action, for a stated reason); Failed and Interrupted are bad.</summary>
    public static string Tone(ScheduledActionOutcome outcome) => outcome switch
    {
        ScheduledActionOutcome.Succeeded => "tone-ok",
        ScheduledActionOutcome.Started => "tone-rim",
        ScheduledActionOutcome.Skipped => "muted",
        _ => "tone-bad",
    };

    /// <summary>The journal phase as a clause: "stopped while replacing the files".</summary>
    public static string Label(RestorePhase phase) => phase switch
    {
        RestorePhase.Replacing => "replacing the files",
        RestorePhase.RollingBack => "putting the previous files back",
        RestorePhase.RollbackFailed => "putting the previous files back, which failed",
        _ => phase.ToString(),
    };

    /// <summary>"just now", "4 min ago", "2 h ago", "3 d ago", or the date.</summary>
    public static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null)
        {
            return "never";
        }

        var span = now - at.Value;
        if (span < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{(int)span.TotalMinutes} min ago";
        }

        if (span < TimeSpan.FromDays(1))
        {
            return $"{(int)span.TotalHours} h ago";
        }

        if (span < TimeSpan.FromDays(14))
        {
            return $"{(int)span.TotalDays} d ago";
        }

        return at.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The forward-looking counterpart of <see cref="Ago"/>, for the next scheduled action: "now", "in 12 min",
    /// "03:00" when it falls later today, "tomorrow 03:00", otherwise the local date and time; "none" for null.
    /// </summary>
    public static string Until(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null)
        {
            return "none";
        }

        var span = at.Value - now;
        if (span < TimeSpan.FromMinutes(1))
        {
            return "now";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"in {(int)span.TotalMinutes} min";
        }

        var local = at.Value.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today)
        {
            return clock;
        }

        if (local.Date == today.AddDays(1))
        {
            return $"tomorrow {clock}";
        }

        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Bytes(long? bytes)
    {
        if (bytes is null)
        {
            return string.Empty;
        }

        double value = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>A working set in gigabytes with one decimal, always in GB: "6.2 GB" (B7).</summary>
    public static string Gigabytes(long bytes) =>
        (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    /// <summary>A share as a whole-number percent, rounded half away from zero: "14 %" (B7).</summary>
    public static string Percent(double percent) =>
        Math.Round(percent, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " %";

    /// <summary>The resource line shown while a process is live: "RAM 6.2 GB · CPU 14 %" (B7).</summary>
    public static string Telemetry(InstanceTelemetry sample) =>
        $"RAM {Gigabytes(sample.WorkingSetBytes)} · CPU {Percent(sample.CpuPercent)}";

    /// <summary>The short text of the mod badge (B8), whose full wording is <see cref="ModsChangedTitle"/>.</summary>
    public const string ModsChangedLabel = "changed since launch";

    /// <summary>
    /// The mod badge's hover title (B8). Deliberately about the metadata: the manager knows a launch was
    /// issued after the mod changed, not that the new files are installed.
    /// </summary>
    public const string ModsChangedTitle = "Mod metadata changed since the last launch";

    public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Stamp(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Plural(int count, string singular, string? plural = null) =>
        count == 1 ? $"{count} {singular}" : $"{count} {plural ?? singular + "s"}";
}
