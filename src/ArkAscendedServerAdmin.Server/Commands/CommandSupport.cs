using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.Processes;
using ArkAscendedServerAdmin.Launch;
using ArkAscendedServerAdmin.Naming;
using ArkAscendedServerAdmin.Ports;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Server.Commands;

/// <summary>Validation and lookup helpers shared by the command facades.</summary>
internal static class CommandSupport
{
    public const int MaxNameLength = 100;

    public const int MaxSessionNameLength = 200;

    /// <summary>Every slug that a new instance or cluster must not reuse: live instances, clusters, and archived worlds.</summary>
    public static async Task<IReadOnlyList<string>> ReservedSlugsAsync(AppDbContext db, DataRootLayout layout, CancellationToken cancellationToken)
    {
        var reserved = new List<string>();
        reserved.AddRange(await db.Instances.AsNoTracking().Select(i => i.Slug).ToListAsync(cancellationToken));
        reserved.AddRange(await db.Clusters.AsNoTracking().Select(c => c.Slug).ToListAsync(cancellationToken));

        if (Directory.Exists(layout.Archive))
        {
            foreach (var directory in Directory.EnumerateDirectories(layout.Archive))
            {
                var name = Path.GetFileName(directory);
                reserved.Add(name);
                if (Slug.TryParseArchiveDirectoryName(name) is { } archivedSlug)
                {
                    reserved.Add(archivedSlug);
                }
            }
        }

        return reserved;
    }

    public static IReadOnlyList<string> ValidateName(string? name, string what)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add($"{what} name is required.");
        }
        else if (name.Trim().Length > MaxNameLength)
        {
            problems.Add($"{what} name must be {MaxNameLength} characters or fewer.");
        }

        return problems;
    }

    public static IReadOnlyList<string> ValidateSessionName(string? sessionName)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(sessionName))
        {
            problems.Add("Session name is required; it is what players see in the server browser.");
            return problems;
        }

        if (sessionName.Trim().Length > MaxSessionNameLength)
        {
            problems.Add($"Session name must be {MaxSessionNameLength} characters or fewer.");
        }

        if (ReservedKeys.ValidateTypedValue("SessionName", sessionName) is { } problem)
        {
            problems.Add(problem);
        }

        return problems;
    }

    public static IReadOnlyList<string> ValidateLaunchFlags(LaunchFlags flags)
    {
        // Empty typed values mean "not passed"; only a set value has to be usable on the command line.
        var problems = new List<string>();
        if (!string.IsNullOrWhiteSpace(flags.ServerPlatform) && ReservedKeys.ValidateTypedValue("ServerPlatform", flags.ServerPlatform) is { } platformProblem)
        {
            problems.Add(platformProblem);
        }

        if (!string.IsNullOrWhiteSpace(flags.ActiveEvent) && ReservedKeys.ValidateTypedValue("ActiveEvent", flags.ActiveEvent) is { } eventProblem)
        {
            problems.Add(eventProblem);
        }

        problems.AddRange(AdditionalArgs.Validate(flags.AdditionalArgs));
        return problems;
    }

    public static IReadOnlyList<string> ValidatePlayers(int maxPlayers) =>
        maxPlayers is < LaunchArgumentBuilder.MinPlayers or > LaunchArgumentBuilder.MaxPlayers
            ? [$"Max players must be between {LaunchArgumentBuilder.MinPlayers} and {LaunchArgumentBuilder.MaxPlayers}."]
            : [];

    public static IReadOnlyList<string> ValidateBackupSettings(int? intervalMinutes, int? retention)
    {
        var problems = new List<string>();
        if (intervalMinutes is { } interval && interval is < 1 or > 10080)
        {
            problems.Add("Backup interval must be between 1 and 10080 minutes, or left blank to use the default.");
        }

        if (retention is { } keep && keep is < 1 or > 1000)
        {
            problems.Add("Backups to keep must be between 1 and 1000, or left blank to use the default.");
        }

        return problems;
    }

    /// <summary>One id per line; blank lines are dropped, everything else is kept verbatim after trimming.</summary>
    public static string NormalizeWhitelist(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join("\r\n", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Copies the typed flags into a fresh owned entity so EF tracks the change as a whole.</summary>
    public static void CopyLaunchFlags(LaunchFlags source, LaunchFlags target)
    {
        target.NoBattlEye = source.NoBattlEye;
        target.ServerPlatform = Trimmed(source.ServerPlatform);
        target.ExclusiveJoin = source.ExclusiveJoin;
        target.NoWildBabies = source.NoWildBabies;
        target.UseStore = source.UseStore;
        target.ConvertToStore = source.ConvertToStore;
        target.ServerGameLogIncludeTribeLogs = source.ServerGameLogIncludeTribeLogs;
        target.ServerRconOutputTribeLogs = source.ServerRconOutputTribeLogs;
        target.ActiveEvent = Trimmed(source.ActiveEvent);
        target.AdditionalArgs = Trimmed(source.AdditionalArgs);
    }

    public static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The web UI's first bound port, which the port allocator treats as occupied.</summary>
    public static int HostPort(HostConfiguration host)
    {
        var ports = KestrelPorts.Parse(host.BindUrls);
        return ports.Count > 0 ? ports[0] : 0;
    }

    public static async Task<IReadOnlyList<PortOwner>> PortOwnersAsync(AppDbContext db, int? excludeInstanceId, CancellationToken cancellationToken) =>
        await db.Instances.AsNoTracking()
            .Where(i => excludeInstanceId == null || i.Id != excludeInstanceId)
            .Select(i => new PortOwner(i.Name, i.GamePort, i.RconPort))
            .ToListAsync(cancellationToken);

    public static InstanceSummary ToSummary(Instance instance, BackupRecord? lastBackup) =>
        new(
            instance.Id,
            instance.Name,
            instance.Slug,
            instance.ClusterId,
            instance.Map?.Key ?? string.Empty,
            instance.Map?.Name ?? string.Empty,
            instance.SessionName,
            instance.GamePort,
            instance.RconPort,
            instance.MaxPlayers,
            instance.Mods.Count,
            lastBackup);

    public static Task<BackupRecord?> LastBackupAsync(AppDbContext db, int instanceId, CancellationToken cancellationToken) =>
        db.BackupRecords.AsNoTracking()
            .Where(b => b.InstanceId == instanceId)
            .OrderByDescending(b => b.Id) // SQLite cannot order by a DateTimeOffset column; ids are monotonic
            .FirstOrDefaultAsync(cancellationToken);
}
