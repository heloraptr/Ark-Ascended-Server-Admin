using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ArkAscendedServerAdmin.Infrastructure.Rcon;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Rcon;

public class CoreRconClientTests
{
    [Fact]
    public async Task ListenerThatNeverAnswers_FailsWithTimeout_WithinTheTimeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var client = new CoreRconClient(NullLogger<CoreRconClient>.Instance);
            var timeout = TimeSpan.FromSeconds(2);
            var stopwatch = Stopwatch.StartNew();

            var failure = await Assert.ThrowsAsync<RconException>(() =>
                client.ExecuteAsync(new RconEndpoint(port, "secret"), RconCommands.ListPlayers, timeout, TestContext.Current.CancellationToken));

            Assert.Equal(RconFailure.Timeout, failure.Failure);
            Assert.True(stopwatch.Elapsed < timeout + TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RefusedPort_FailsWithConnect()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var client = new CoreRconClient(NullLogger<CoreRconClient>.Instance);

        var failure = await Assert.ThrowsAsync<RconException>(() =>
            client.ExecuteAsync(new RconEndpoint(port, "secret"), RconCommands.ListPlayers, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Equal(RconFailure.Connect, failure.Failure);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsCancellation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var client = new CoreRconClient(NullLogger<CoreRconClient>.Instance);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.ExecuteAsync(new RconEndpoint(port, "secret"), RconCommands.ListPlayers, TimeSpan.FromSeconds(30), cancellation.Token));
        }
        finally
        {
            listener.Stop();
        }
    }
}
