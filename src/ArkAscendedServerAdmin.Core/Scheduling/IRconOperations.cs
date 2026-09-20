using ArkAscendedServerAdmin.Commands;

namespace ArkAscendedServerAdmin.Scheduling;

/// <summary>
/// Sends one RCON command to a running instance and echoes both the command and the reply to the
/// instance console, exactly as the console page's send box does (B3). Session-bound: the instance must
/// have a live session, and the endpoint is resolved from the generated <c>GameUserSettings.ini</c>. No
/// authorization check happens here — <c>IInstanceCommands.SendRconAsync</c> guards the interactive path
/// and then delegates to this service, while the scheduled-action runner calls it directly.
/// </summary>
public interface IRconOperations
{
    /// <summary>
    /// Executes <paramref name="command"/> on the instance and returns the reply. Fails (never throws) with a
    /// full-sentence message when the command is blank, the instance has no live process, its RCON endpoint
    /// cannot be read, or the server does not answer within the configured timeout.
    /// </summary>
    Task<CommandResult<string>> ExecuteAsync(int instanceId, string command, CancellationToken cancellationToken);
}
