using System.Globalization;
using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Commands;

public class SettingsCommandsTests
{
    [Fact]
    public async Task EveryMethod_RefusesWhenTheGuardDenies()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        host.Guard.Deny = true;

        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.SettingsCommands.GetAppSettingsAsync(ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.SettingsCommands.SaveAppSettingsAsync(new Configuration.AppSettings(), ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.SettingsCommands.GetHostConfigurationAsync(ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.SettingsCommands.ExportConfigBackupAsync(ct));
        await host.Exporter.DidNotReceiveWithAnyArgs().ExportAsync(default!, ct);
    }

    [Fact]
    public async Task AppSettings_RoundTripThroughTheStore_AndInvalidValuesAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);

        var defaults = await host.SettingsCommands.GetAppSettingsAsync(ct);
        await host.SettingsCommands.SaveAppSettingsAsync(defaults with { StaggerDelaySeconds = 5, CurseForgeApiKey = "key" }, ct);
        var saved = await host.SettingsCommands.GetAppSettingsAsync(ct);
        await Assert.ThrowsAsync<ArgumentException>(() => host.SettingsCommands.SaveAppSettingsAsync(defaults with { GamePortStart = 0 }, ct));

        Assert.Equal(30, defaults.StaggerDelaySeconds);
        Assert.Equal((5, "key"), (saved.StaggerDelaySeconds, saved.CurseForgeApiKey));
        Assert.Equal(5, (await host.SettingsCommands.GetAppSettingsAsync(ct)).StaggerDelaySeconds);
    }

    [Fact]
    public async Task HostConfiguration_IsTheInjectedRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);

        Assert.Same(host.Host, await host.SettingsCommands.GetHostConfigurationAsync(ct));
    }

    [Fact]
    public async Task ExportConfigBackup_WritesATimestampedFileUnderExports()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);

        var path = await host.SettingsCommands.ExportConfigBackupAsync(ct);

        var expectedStamp = CommandTestHost.Now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        Assert.Equal(Path.Combine(host.Root.Layout.Exports, $"config-{expectedStamp}.db"), path);
        await host.Exporter.Received(1).ExportAsync(path, Arg.Any<CancellationToken>());
    }
}

public class MaintenanceCommandsTests
{
    [Fact]
    public async Task EveryMethod_RefusesWhenTheGuardDenies()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        host.Guard.Deny = true;

        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Maintenance.RetryInstallAsync(ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Maintenance.StartUpdateAsync(true, ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Maintenance.RetryEntryAsync(1, ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Maintenance.SkipEntryAsync(1, ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Maintenance.ResumeMaintenanceAsync(ct));
        await host.StartupControl.DidNotReceiveWithAnyArgs().RetryInstallAsync(ct);
        await host.UpdateService.DidNotReceiveWithAnyArgs().StartUpdateAsync(default, ct);
        await host.Recovery.DidNotReceiveWithAnyArgs().ResumeAsync(ct);
    }

    [Fact]
    public async Task InstallUpdateRetryAndSkip_PassThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        host.UpdateService.StartUpdateAsync(true, Arg.Any<CancellationToken>()).Returns(OperationOutcome.Rejected("Update already running."));
        host.UpdateService.RetryEntryAsync(3, Arg.Any<CancellationToken>()).Returns(OperationOutcome.Success);
        host.UpdateService.SkipEntryAsync(4, Arg.Any<CancellationToken>()).Returns(OperationOutcome.Rejected("Not failed."));

        await host.Maintenance.RetryInstallAsync(ct);
        var update = await host.Maintenance.StartUpdateAsync(true, ct);
        var retry = await host.Maintenance.RetryEntryAsync(3, ct);
        var skip = await host.Maintenance.SkipEntryAsync(4, ct);

        await host.StartupControl.Received(1).RetryInstallAsync(Arg.Any<CancellationToken>());
        Assert.Equal("Update already running.", update.Error);
        Assert.True(retry.Succeeded);
        Assert.Equal("Not failed.", skip.Error);
    }

    [Theory]
    [InlineData(MaintenancePhase.None)]
    [InlineData(MaintenancePhase.Installing)]
    public async Task ResumeMaintenance_IsRejectedWhenNothingIsInterrupted(MaintenancePhase phase)
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        host.UpdateService.Current.Returns(new MaintenanceSnapshot(phase, [], null, null));

        var outcome = await host.Maintenance.ResumeMaintenanceAsync(ct);

        Assert.Equal("There is no interrupted update to resume.", outcome.Error);
        await host.Recovery.DidNotReceiveWithAnyArgs().ResumeAsync(ct);
    }

    [Theory]
    [InlineData(MaintenancePhase.Stopping)]
    [InlineData(MaintenancePhase.Updating)]
    [InlineData(MaintenancePhase.Restarting)]
    public async Task ResumeMaintenance_AcceptsAndRunsTheRecoveryInTheBackground(MaintenancePhase phase)
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        host.UpdateService.Current.Returns(new MaintenanceSnapshot(phase, [], DateTimeOffset.UnixEpoch, null));
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Recovery.ResumeAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            resumed.TrySetResult();
            return Task.CompletedTask;
        });

        var outcome = await host.Maintenance.ResumeMaintenanceAsync(ct);

        Assert.True(outcome.Succeeded);
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await host.Recovery.Received(1).ResumeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ResumeMaintenance_SwallowsARecoveryFailure_BecauseTheDashboardFollowsTheSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        host.UpdateService.Current.Returns(new MaintenanceSnapshot(MaintenancePhase.Updating, [], DateTimeOffset.UnixEpoch, "SteamCMD exited 8"));
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Recovery.ResumeAsync(Arg.Any<CancellationToken>()).Returns<Task>(_ =>
        {
            called.TrySetResult();
            throw new InvalidOperationException("SteamCMD is missing.");
        });

        var outcome = await host.Maintenance.ResumeMaintenanceAsync(ct);

        Assert.True(outcome.Succeeded);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
}
