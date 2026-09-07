using System.Net;
using System.Net.Sockets;
using ArkAscendedServerAdmin.Rcon;
using CoreRCON;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Rcon;

/// <summary>
/// <see cref="IRconClient"/> over CoreRCON (plan step 23, Spike A): a fresh <see cref="RCON"/> per call to
/// loopback, connect + authenticate + send under one linked timeout, and a guarded <c>Dispose()</c>
/// because CoreRCON throws <see cref="SocketException"/> 10057 when the socket never connected.
/// </summary>
public sealed class CoreRconClient(ILogger<CoreRconClient> logger) : IRconClient
{
    public async Task<string> ExecuteAsync(RconEndpoint endpoint, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The RCON timeout must be positive.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;

        var rcon = new RCON(IPAddress.Loopback, checked((ushort)endpoint.Port), endpoint.Password, checked((uint)timeout.TotalMilliseconds));
        try
        {
            await rcon.ConnectAsync().WaitAsync(token);
            if (!rcon.Authenticated)
            {
                throw new RconException(RconFailure.Authentication, $"RCON on port {endpoint.Port} rejected the password.");
            }

            return await rcon.SendCommandAsync(command, timeout).WaitAsync(token);
        }
        catch (RconException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            throw new RconException(RconFailure.Timeout, $"RCON '{command}' on port {endpoint.Port} exceeded {timeout.TotalSeconds:0.#} s.", ex);
        }
        catch (AuthenticationException ex)
        {
            throw new RconException(RconFailure.Authentication, $"RCON on port {endpoint.Port} rejected the password.", ex);
        }
        catch (Exception ex) when (FindSocketException(ex) is { } socketException)
        {
            throw new RconException(RconFailure.Connect, $"RCON on port {endpoint.Port} could not connect: {socketException.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new RconException(RconFailure.Protocol, $"RCON '{command}' on port {endpoint.Port} failed: {ex.Message}", ex);
        }
        finally
        {
            SafeDispose(rcon);
        }
    }

    private static SocketException? FindSocketException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketException)
            {
                return socketException;
            }
        }

        return null;
    }

    private void SafeDispose(RCON rcon)
    {
        try
        {
            rcon.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "CoreRCON Dispose threw (expected when the socket never connected).");
        }
    }
}
