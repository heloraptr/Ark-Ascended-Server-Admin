using System.Diagnostics;

namespace ArkAscendedServerAdmin.Infrastructure.Processes;

/// <summary>
/// The one <c>Process.Start</c> of a game launch (B4), behind an interface so the process-manager tests can launch a
/// stand-in process and exercise a successful relaunch. Throws what <see cref="Process.Start(ProcessStartInfo)"/> throws.
/// </summary>
public interface IGameProcessStarter
{
    Process Start(ProcessStartInfo startInfo);
}

/// <summary>The production starter: <see cref="Process.Start(ProcessStartInfo)"/>, nothing more.</summary>
public sealed class GameProcessStarter : IGameProcessStarter
{
    public Process Start(ProcessStartInfo startInfo) =>
        Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned no process.");
}
