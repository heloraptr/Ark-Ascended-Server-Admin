using System.Globalization;
using System.Management;
using ArkAscendedServerAdmin.Processes;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Processes;

/// <summary>
/// Lists every <c>ArkAscendedServer.exe</c> through WMI <c>Win32_Process</c> (plan step 21, Spike B):
/// <c>ExecutablePath</c> is the junction path for managed instances and <c>CreationDate</c> equals
/// <c>Process.StartTime</c> to the millisecond. Fields WMI withholds (access denied, exiting process) are
/// returned as null so the matcher treats the process as unmanaged. <see cref="ReadRow"/> is the targeted
/// single-pid read the session probe uses (B0); it reports whether the identity fields were all readable.
/// </summary>
public sealed class WmiGameProcessEnumerator(ILogger<WmiGameProcessEnumerator> logger) : IGameProcessEnumerator
{
    public const string ExecutableName = "ArkAscendedServer.exe";

    private const string Columns = "SELECT ProcessId, ExecutablePath, CommandLine, CreationDate FROM Win32_Process WHERE ";

    private const string Query = Columns + "Name = '" + ExecutableName + "'";

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

    public ProcessRowRead ReadRow(int pid)
    {
        using var searcher = new ManagementObjectSearcher(Columns + "ProcessId = " + pid.ToString(CultureInfo.InvariantCulture));
        using var results = searcher.Get();
        foreach (var result in results)
        {
            using (result)
            {
                var info = Read(result);
                if (info is null)
                {
                    return new ProcessRowRead(ProcessRowStatus.Incomplete, null);
                }

                var complete = info.ExecutablePath is not null && info.CreationTime != DateTimeOffset.MinValue;
                return new ProcessRowRead(complete ? ProcessRowStatus.Complete : ProcessRowStatus.Incomplete, info);
            }
        }

        return ProcessRowRead.Missing;
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
