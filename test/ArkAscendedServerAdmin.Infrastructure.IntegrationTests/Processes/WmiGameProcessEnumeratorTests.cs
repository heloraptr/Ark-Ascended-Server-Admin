using System.Diagnostics;
using ArkAscendedServerAdmin.Infrastructure.Processes;
using ArkAscendedServerAdmin.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Processes;

/// <summary>The targeted single-pid read against the real WMI provider, using the test process itself.</summary>
public class WmiGameProcessEnumeratorTests
{
    [Fact]
    public void ReadRow_OfTheCurrentProcess_IsCompleteAndMatchesItsStartTime()
    {
        var enumerator = new WmiGameProcessEnumerator(NullLogger<WmiGameProcessEnumerator>.Instance);
        using var current = Process.GetCurrentProcess();

        var read = enumerator.ReadRow(current.Id);

        Assert.Equal(ProcessRowStatus.Complete, read.Status);
        Assert.NotNull(read.Row);
        Assert.Equal(current.Id, read.Row.Pid);
        Assert.False(string.IsNullOrEmpty(read.Row.ExecutablePath));
        Assert.True((read.Row.CreationTime - new DateTimeOffset(current.StartTime)).Duration() <= ProcessMatcher.StartTimeTolerance);
    }

    [Fact]
    public void ReadRow_OfAPidNobodyHas_IsMissing()
    {
        var enumerator = new WmiGameProcessEnumerator(NullLogger<WmiGameProcessEnumerator>.Instance);

        Assert.Equal(ProcessRowRead.Missing, enumerator.ReadRow(int.MaxValue - 7));
    }
}
