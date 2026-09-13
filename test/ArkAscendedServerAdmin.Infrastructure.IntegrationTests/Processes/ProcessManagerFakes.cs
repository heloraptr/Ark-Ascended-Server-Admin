using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Firewall;
using ArkAscendedServerAdmin.Ini;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Rcon;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Processes;

internal sealed class StubEnumerator(IReadOnlyList<GameProcessInfo> processes) : IGameProcessEnumerator
{
    public IReadOnlyList<GameProcessInfo> Enumerate() => processes;
}

/// <summary>Succeeds or throws the configured failure; every call is recorded.</summary>
internal sealed class FakeRconClient(RconFailure? failure) : IRconClient
{
    public List<string> Commands { get; } = [];

    public Task<string> ExecuteAsync(RconEndpoint endpoint, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (Commands)
        {
            Commands.Add(command);
        }

        return failure is { } kind
            ? throw new RconException(kind, $"fake {kind}")
            : Task.FromResult(command == RconCommands.ListPlayers ? RconCommands.NoPlayersReply : "ok");
    }
}

/// <summary>An output source that produces nothing and runs until cancelled; records the options it was given.</summary>
internal sealed class FakeOutputSourceFactory : IOutputSourceFactory, IOutputSource
{
    public List<string> Paths { get; } = [];

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OutputSourceOptions? LastOptions { get; private set; }

    /// <summary>The manager starts the tail on a background task; tests await this before reading <see cref="LastOptions"/>.</summary>
    public Task Started => _started.Task;

    public IOutputSource ForLogFile(string logPath)
    {
        Paths.Add(logPath);
        return this;
    }

    public async Task RunAsync(OutputSourceOptions options, Func<OutputLine, ValueTask> onLine, CancellationToken cancellationToken)
    {
        LastOptions = options;
        _started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}

internal sealed class FakeConfigWriter(string? generatedGameUserSettings) : IGeneratedConfigWriter
{
    public Task<GeneratedConfig> WriteAsync(int instanceId, CancellationToken cancellationToken) =>
        Task.FromResult(new GeneratedConfig(string.Empty, generatedGameUserSettings ?? string.Empty, [], null, []));

    public Task<string?> ReadGeneratedGameUserSettingsAsync(string slug, CancellationToken cancellationToken) =>
        Task.FromResult(generatedGameUserSettings);
}

internal sealed class FakeLayoutService : IInstanceLayoutService
{
    public Task EnsureAsync(string slug, CancellationToken cancellationToken) => Task.CompletedTask;

    public bool IsComplete(string slug) => true;

    public Task RemoveJunctionsAsync(string slug, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string?> RetireAsync(string slug, bool keepWorldData, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

internal sealed class FakeFirewall : IFirewallRules
{
    public List<(int InstanceId, int GamePort)> Ensured { get; } = [];

    public void EnsureInstanceRules(int instanceId, int gamePort) => Ensured.Add((instanceId, gamePort));

    public void RemoveInstanceRules(int instanceId)
    {
    }
}

internal sealed class RecordingConsole : IConsoleService
{
    private readonly Dictionary<string, List<ConsoleLine>> _channels = [];

    public event Action<string, ConsoleLine>? LineAppended;

    public event Action<string>? Cleared;

    public IReadOnlyList<ConsoleLine> Snapshot(string channel)
    {
        lock (_channels)
        {
            return _channels.TryGetValue(channel, out var lines) ? [.. lines] : [];
        }
    }

    public void Append(string channel, ConsoleLine line)
    {
        lock (_channels)
        {
            if (!_channels.TryGetValue(channel, out var lines))
            {
                lines = [];
                _channels[channel] = lines;
            }

            lines.Add(line);
        }

        LineAppended?.Invoke(channel, line);
    }

    public void Clear(string channel)
    {
        lock (_channels)
        {
            _channels.Remove(channel);
        }

        Cleared?.Invoke(channel);
    }
}
