using System.Globalization;
using System.Management;
using ArkAscendedServerAdmin.Processes;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Processes;

/// <summary>
/// Lists every <c>ArkAscendedServer.exe</c> through WMI <c>Win32_Process</c> (plan step 21, Spike B):
/// <c>ExecutablePath</c> is the junction path for managed instances and <c>CreationDate</c> equals
/// <c>Process.StartTime</c> to the millisecond. Fields WMI withholds (access denied, exiting process) are
/// returned as null so the matcher treats the process as unmanaged.
/// </summary>
public sealed class WmiGameProcessEnumerator(ILogger<WmiGameProcessEnumerator> logger) : IGameProcessEnumerator
{
    public const string ExecutableName = "ArkAscendedServer.exe";

    private const string Query =
        "SELECT ProcessId, ExecutablePath, CommandLine, CreationDate FROM Win32_Process WHERE Name = '" + ExecutableName + "'";

    public IReadOnlyList<GameProcessInfo> Enumerate()
    {
        var processes = new List<GameProcessInfo>();
        using var searcher = new ManagementObjectSearcher(Query);
        using var results = searcher.Get();
        foreach (var result in results)
        {
            using (result)
            {
                var info = Read(result);
                if (info is not null)
                {
                    processes.Add(info);
                }
            }
        }

        return processes;
    }

    private GameProcessInfo? Read(ManagementBaseObject result)
    {
        try
        {
            var pid = Convert.ToInt32(result["ProcessId"], CultureInfo.InvariantCulture);
            var creationText = result["CreationDate"] as string;
            var creationTime = string.IsNullOrEmpty(creationText)
                ? DateTimeOffset.MinValue
                : new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(creationText));

            return new GameProcessInfo(pid, result["ExecutablePath"] as string, result["CommandLine"] as string, creationTime);
        }
        catch (Exception ex) when (ex is ManagementException or InvalidCastException or FormatException or OverflowException)
        {
            logger.LogWarning(ex, "Skipping a Win32_Process row that could not be read.");
            return null;
        }
    }
}
