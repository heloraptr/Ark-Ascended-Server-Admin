using System.Threading.Channels;

namespace ArkAscendedServerAdmin.Processes;

/// <summary>
/// The channel between the process manager and the crash policy (B0, consumed by B4). The manager posts one
/// <see cref="RecoveryRequest"/> per confirmed exit after its own cleanup and exit signal are done, so nothing
/// in the exit path ever waits on recovery. Unbounded: exits are rare and every one must be seen.
/// </summary>
public sealed class RecoveryRequests
{
    private readonly Channel<RecoveryRequest> _channel = Channel.CreateUnbounded<RecoveryRequest>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<RecoveryRequest> Reader => _channel.Reader;

    /// <summary>Never blocks and never fails while the channel is open; a completed channel drops the request.</summary>
    public bool TryPost(RecoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _channel.Writer.TryWrite(request);
    }
}
