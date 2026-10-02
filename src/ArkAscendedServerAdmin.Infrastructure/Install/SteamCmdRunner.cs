using System.Globalization;
using System.IO.Compression;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Install;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Install;

/// <summary>
/// The SteamCMD runner (plan step 20). Downloads and extracts SteamCMD on first use, then runs
/// <c>+force_install_dir &lt;Server&gt; +login anonymous +app_update 2430930 [validate] +quit</c> in that
/// order, streaming every line to the <see cref="ConsoleChannels.SteamCmd"/> console. Exit code 7 (the
/// first-run self-update) re-runs immediately; any other failure — a non-zero exit, a download error, or
/// exit 0 without a verified manifest — consumes one attempt of <see cref="SteamCmdRetryPolicy"/>.
/// Cancellation kills SteamCMD and returns a failure. Progress is published through
/// <see cref="ISteamCmdProgressMonitor"/>. One run at a time; a concurrent call is refused.
/// </summary>
public sealed class SteamCmdRunner(
    DataRootLayout layout,
    IHttpClientFactory httpClientFactory,
    IConsoleService console,
    IGameInstallChecker installChecker,
    ISteamCmdProcessLauncher launcher,
    SteamCmdRetryPolicy retryPolicy,
    TimeProvider timeProvider,
    ILogger<SteamCmdRunner> logger) : ISteamCmdRunner, ISteamCmdProgressMonitor
{
    public const string HttpClientName = "steamcmd";

    public static readonly Uri DownloadUri = new("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip");

    /// <summary>How many consecutive exit-7 runs are tolerated before they count as a failure (guards against a broken self-update loop).</summary>
    private const int MaxConsecutiveSelfUpdates = 3;

    /// <summary>Exit code reported when no process ran (download failure, cancellation).</summary>
    private const int NoExitCode = -1;

    private readonly SemaphoreSlim _runGate = new(1, 1);
    private SteamCmdProgress? _current;

    public SteamCmdProgress? Current => Volatile.Read(ref _current);

    public event Action<SteamCmdProgress?>? Changed;

    public async Task<SteamCmdResult> InstallOrUpdateAsync(bool validate, CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
        {
            return SteamCmdResult.Failure(NoExitCode, "SteamCMD is already running.");
        }

        try
        {
            return await RunWithRetriesAsync(validate, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Warn("SteamCMD run canceled.");
            return SteamCmdResult.Failure(NoExitCode, "SteamCMD run canceled.");
        }
        finally
        {
            SetProgress(null);
            _runGate.Release();
        }
    }

    private async Task<SteamCmdResult> RunWithRetriesAsync(bool validate, CancellationToken cancellationToken)
    {
        var failedAttempts = 0;
        var consecutiveSelfUpdates = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetProgress(null);

            var attempt = failedAttempts + 1;
            var outcome = await RunOnceAsync(validate, attempt, cancellationToken);

            if (outcome.ExitCode == 7 && consecutiveSelfUpdates < MaxConsecutiveSelfUpdates)
            {
                consecutiveSelfUpdates++;
                Info("SteamCMD updated itself (exit code 7); running again.");
                continue;
            }

            consecutiveSelfUpdates = 0;
            if (outcome.Succeeded)
            {
                Info("SteamCMD finished and the install is verified.");
                return SteamCmdResult.Success;
            }

            failedAttempts++;
            var delay = retryPolicy.DelayAfter(failedAttempts);
            if (delay is null)
            {
                var error = $"SteamCMD failed after {failedAttempts} attempt(s): {outcome.Error}";
                Error(error);
                logger.LogError("{Error}", error);
                return SteamCmdResult.Failure(outcome.ExitCode, error);
            }

            Warn($"Attempt {attempt} failed ({outcome.Error}); retrying in {Describe(delay.Value)} (attempt {attempt + 1} of {retryPolicy.MaxAttempts}).");
            logger.LogWarning("SteamCMD attempt {Attempt} failed with exit code {ExitCode}: {Error}. Retrying in {Delay}.", attempt, outcome.ExitCode, outcome.Error, delay.Value);
            await Task.Delay(delay.Value, timeProvider, cancellationToken);
        }
    }

    private async Task<SteamCmdResult> RunOnceAsync(bool validate, int attempt, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureSteamCmdAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "SteamCMD download failed.");
            return SteamCmdResult.Failure(NoExitCode, $"SteamCMD download failed: {ex.Message}");
        }

        var arguments = BuildArguments(validate);
        Info($"Attempt {attempt}: steamcmd.exe {string.Join(' ', arguments)}");
        logger.LogInformation("Starting SteamCMD (attempt {Attempt}): {Arguments}", attempt, string.Join(' ', arguments));

        var sawSuccessLine = false;
        var exitCode = await launcher.RunAsync(
            new SteamCmdLaunch(layout.SteamCmdExecutable, arguments, layout.SteamCmd),
            line =>
            {
                Append(line, ConsoleLineKind.Output);
                if (SteamCmdOutput.TryParseProgress(line, out var progress))
                {
                    SetProgress(progress);
                }
                else if (SteamCmdOutput.IsSuccess(line, DataRootLayout.ServerAppId))
                {
                    sawSuccessLine = true;
                }
            },
            line => Append(line, ConsoleLineKind.Warning),
            cancellationToken);

        logger.LogInformation("SteamCMD exited with code {ExitCode}.", exitCode);
        if (exitCode != 0)
        {
            return SteamCmdResult.Failure(exitCode, $"SteamCMD exited with code {exitCode}");
        }

        var status = installChecker.Check();
        if (!status.IsComplete)
        {
            return SteamCmdResult.Failure(0, $"SteamCMD exited with code 0 but the install is not verified: {status.Detail}");
        }

        if (!sawSuccessLine)
        {
            logger.LogDebug("SteamCMD did not print the success line, but the manifest verifies; treating as success.");
        }

        return SteamCmdResult.Success;
    }

    private async Task EnsureSteamCmdAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(layout.SteamCmdExecutable))
        {
            return;
        }

        Directory.CreateDirectory(layout.SteamCmd);
        var zipPath = Path.Combine(layout.SteamCmd, "steamcmd.zip.download");
        Info($"Downloading SteamCMD from {DownloadUri} ...");
        logger.LogInformation("Downloading SteamCMD from {Uri} to {Directory}.", DownloadUri, layout.SteamCmd);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await response.Content.CopyToAsync(target, cancellationToken);
            }

            ZipFile.ExtractToDirectory(zipPath, layout.SteamCmd, overwriteFiles: true);
        }
        finally
        {
            try
            {
                File.Delete(zipPath);
            }
            catch (IOException)
            {
                // Leave it; the next download overwrites it.
            }
        }

        if (!File.Exists(layout.SteamCmdExecutable))
        {
            throw new InvalidDataException($"The SteamCMD archive did not contain {Path.GetFileName(layout.SteamCmdExecutable)}.");
        }

        Info($"SteamCMD extracted to {layout.SteamCmd}.");
    }

    private List<string> BuildArguments(bool validate)
    {
        // +force_install_dir must precede +login (Phase 2 spike).
        var arguments = new List<string>
        {
            "+force_install_dir",
            layout.Server,
            "+login",
            "anonymous",
            "+app_update",
            DataRootLayout.ServerAppId.ToString(CultureInfo.InvariantCulture),
        };
        if (validate)
        {
            arguments.Add("validate");
        }

        arguments.Add("+quit");
        return arguments;
    }

    private void SetProgress(SteamCmdProgress? progress)
    {
        var previous = Interlocked.Exchange(ref _current, progress);
        if (ReferenceEquals(previous, progress) || (previous is null && progress is null))
        {
            return;
        }

        try
        {
            Changed?.Invoke(progress);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A SteamCMD progress subscriber threw.");
        }
    }

    private void Info(string text) => Append(text, ConsoleLineKind.Info);

    private void Warn(string text) => Append(text, ConsoleLineKind.Warning);

    private void Error(string text) => Append(text, ConsoleLineKind.Error);

    private void Append(string text, ConsoleLineKind kind) =>
        console.Append(ConsoleChannels.SteamCmd, new ConsoleLine(timeProvider.GetUtcNow(), text, kind));

    private static string Describe(TimeSpan delay) =>
        delay.TotalSeconds < 60
            ? $"{delay.TotalSeconds:0.###} s"
            : $"{delay.TotalMinutes:0.#} min";
}
