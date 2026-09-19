using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Rcon;
using ArkAscendedServerAdmin.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.Scheduling;

/// <summary>
/// The one RCON send path shared by the console page and the scheduled-action runner (B3): the instance
/// must have a live process, the endpoint is read from the generated <c>GameUserSettings.ini</c> (the file the
/// running process actually read), and the command and every reply line are echoed to the instance console.
/// No authorization check here; the facade guards the interactive path before delegating.
/// </summary>
public sealed class RconOperations(
    IDbContextFactory<AppDbContext> contextFactory,
    IAppSettingsStore settings,
    IProcessManager processManager,
    IGeneratedConfigWriter generatedConfig,
    IRconClient rcon,
    IConsoleService console,
    TimeProvider timeProvider) : IRconOperations
{
    public async Task<CommandResult<string>> ExecuteAsync(int instanceId, string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return CommandResult<string>.Fail("Type a command first.");
        }

        var runtime = processManager.GetRuntime(instanceId);
        if (!runtime.HasLiveProcess)
        {
            return CommandResult<string>.Fail("The instance is not running, so there is nothing to send the command to.");
        }

        string slug;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            slug = await db.Instances.AsNoTracking().Where(i => i.Id == instanceId).Select(i => i.Slug).SingleOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Instance {instanceId} does not exist.");
        }

        var generated = await generatedConfig.ReadGeneratedGameUserSettingsAsync(slug, cancellationToken);
        if (generated is null)
        {
            return CommandResult<string>.Fail("The generated GameUserSettings.ini is missing, so the RCON password is unknown.");
        }

        var endpoint = RconCredentials.TryRead(generated, out var problem);
        if (endpoint is null)
        {
            return CommandResult<string>.Fail(problem ?? "RCON credentials could not be read.");
        }

        var channel = ConsoleChannels.Instance(instanceId);
        var trimmed = command.Trim();
        console.Append(channel, new ConsoleLine(timeProvider.GetUtcNow(), $"> {trimmed}", ConsoleLineKind.Info));
        try
        {
            var timeout = TimeSpan.FromSeconds((await settings.GetAsync(cancellationToken)).RconCommandTimeoutSeconds);
            var reply = await rcon.ExecuteAsync(endpoint, trimmed, timeout, cancellationToken);
            var text = string.IsNullOrWhiteSpace(reply) ? "(no reply)" : reply.TrimEnd();
            foreach (var line in text.Split('\n'))
            {
                console.Append(channel, new ConsoleLine(timeProvider.GetUtcNow(), line.TrimEnd('\r'), ConsoleLineKind.Output));
            }

            return CommandResult<string>.Ok(text);
        }
        catch (RconException ex)
        {
            console.Append(channel, new ConsoleLine(timeProvider.GetUtcNow(), $"RCON {ex.Failure}: {ex.Message}", ConsoleLineKind.Error));
            return CommandResult<string>.Fail($"RCON {ex.Failure.ToString().ToLowerInvariant()} failure: {ex.Message}");
        }
    }
}
