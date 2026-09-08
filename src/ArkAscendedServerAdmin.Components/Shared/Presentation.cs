using System.Globalization;
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
        _ => state.ToString(),
    };

    /// <summary>The short explanation shown under the state where the state itself does not say enough.</summary>
    public static string? Hint(InstanceRuntime runtime) => runtime.State switch
    {
        InstanceState.StartingUnconfirmed => "Alive for over 10 minutes without answering RCON.",
        InstanceState.Unreachable => runtime.Detail ?? "Alive, but RCON keeps failing. Check ServerAdminPassword and RCONPort.",
        InstanceState.Unknown => runtime.Detail ?? "More than one process matched; nothing is done automatically.",
        InstanceState.IdentityUnpersisted => runtime.Detail ?? "The process runs but its PID could not be saved.",
        InstanceState.Starting => runtime.LastMarker switch
        {
            Consoles.StartupMarker.WorldLoaded => "World loaded, waiting to advertise.",
            Consoles.StartupMarker.Advertising => "Advertising, waiting for RCON.",
            _ => "Loading the world.",
        },
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

    public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Stamp(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Plural(int count, string singular, string? plural = null) =>
        count == 1 ? $"{count} {singular}" : $"{count} {plural ?? singular + "s"}";
}
