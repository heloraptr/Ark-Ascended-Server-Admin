using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Provisioning;
using ArkAscendedServerAdmin.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Commands;

public class ConfigCommandsTests
{
    private const string Text = "[ServerSettings]\r\nServerAdminPassword=hunter2\r\n";

    private static async Task<(CommandTestHost Host, int InstanceId)> StartAsync(CancellationToken ct)
    {
        var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var created = await host.Instances.CreateAsync(
            new InstanceDraft { Name = "Solo", MapId = await host.MapIdAsync(ct), SessionName = "Solo", GamePort = 7777, RconPort = 27020, ConfigSource = ConfigSourceKind.Blank }, ct);
        Assert.True(created.Succeeded, created.Error);
        return (host, created.Value);
    }

    [Fact]
    public async Task EveryMethod_RefusesWhenTheGuardDenies()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, instanceId) = await StartAsync(ct);
        using (host)
        {
            host.Guard.Deny = true;
            var owner = IniOwner.ForInstance(instanceId);

            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Config.LoadIniAsync(owner, IniFile.Game, ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Config.SaveIniAsync(owner, IniFile.Game, Text, string.Empty, ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Config.RetryMirrorAsync(owner, IniFile.Game, ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Config.RestoreFromDatabaseAsync(ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Config.GetOverridesAsync(instanceId, ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Config.SaveOverrideAsync(new ExtraOverride { InstanceId = instanceId, Section = "S", Key = "K" }, ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Config.DeleteOverrideAsync(1, ct));
        }
    }

    [Fact]
    public async Task SaveIni_RoundTripsThroughTheStore_AndRejectsAStaleHash()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, instanceId) = await StartAsync(ct);
        using (host)
        {
            var owner = IniOwner.ForInstance(instanceId);
            var initial = await host.Config.LoadIniAsync(owner, IniFile.GameUserSettings, ct);

            var saved = await host.Config.SaveIniAsync(owner, IniFile.GameUserSettings, Text, initial.Sha256, ct);
            var stale = await host.Config.SaveIniAsync(owner, IniFile.GameUserSettings, "changed", initial.Sha256, ct);
            var reloaded = await host.Config.LoadIniAsync(owner, IniFile.GameUserSettings, ct);

            Assert.True(saved.Succeeded);
            Assert.Equal(IniSourceStore.ComputeSha256(Text), saved.NewSha256);
            Assert.False(stale.Succeeded);
            Assert.Equal(IniSourceStore.ChangedSinceOpened, stale.Error);
            Assert.Equal(Text, reloaded.Text);
            Assert.False(reloaded.MirrorStale);
        }
    }

    [Fact]
    public async Task RetryMirror_RepairsAMirrorRowThatDriftedFromTheFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, instanceId) = await StartAsync(ct);
        using (host)
        {
            var owner = IniOwner.ForInstance(instanceId);
            var initial = await host.Config.LoadIniAsync(owner, IniFile.Game, ct);
            Assert.True((await host.Config.SaveIniAsync(owner, IniFile.Game, Text, initial.Sha256, ct)).Succeeded);
            await using (var db = host.Db())
            {
                var row = await db.IniDocuments.SingleAsync(d => d.InstanceId == instanceId && d.File == IniFile.Game, ct);
                row.Text = "stale";
                row.Sha256 = "stale";
                await db.SaveChangesAsync(ct);
            }

            Assert.True((await host.Config.LoadIniAsync(owner, IniFile.Game, ct)).MirrorStale);
            var result = await host.Config.RetryMirrorAsync(owner, IniFile.Game, ct);

            Assert.True(result.Succeeded, result.Error);
            Assert.False((await host.Config.LoadIniAsync(owner, IniFile.Game, ct)).MirrorStale);
        }
    }

    [Fact]
    public async Task RestoreFromDatabase_RewritesADeletedSourceFileFromItsMirror()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, instanceId) = await StartAsync(ct);
        using (host)
        {
            var owner = IniOwner.ForInstance(instanceId);
            var initial = await host.Config.LoadIniAsync(owner, IniFile.Game, ct);
            Assert.True((await host.Config.SaveIniAsync(owner, IniFile.Game, Text, initial.Sha256, ct)).Succeeded);
            var path = Path.Combine(host.Root.Layout.InstanceConfigSourceDirectory("solo"), "Game.ini");
            File.Delete(path);

            var result = await host.Config.RestoreFromDatabaseAsync(ct);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(Text, await File.ReadAllTextAsync(path, ct));
        }
    }

    [Fact]
    public async Task SaveOverride_InsertsThenUpdates_TrimmingSectionKeyAndValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, instanceId) = await StartAsync(ct);
        using (host)
        {
            var inserted = await host.Config.SaveOverrideAsync(
                new ExtraOverride { InstanceId = instanceId, File = IniFile.GameUserSettings, Section = " ServerSettings ", Key = " XPMultiplier ", Value = " 2.0 " }, ct);

            Assert.True(inserted.Succeeded, inserted.Error);
            Assert.NotEqual(0, inserted.Value!.Id);
            Assert.Equal(("ServerSettings", "XPMultiplier", "2.0"), (inserted.Value.Section, inserted.Value.Key, inserted.Value.Value));

            var updated = await host.Config.SaveOverrideAsync(
                new ExtraOverride { Id = inserted.Value.Id, InstanceId = instanceId, File = IniFile.Game, Section = "/Script/ShooterGame.ShooterGameMode", Key = "TamingSpeedMultiplier", Value = "3" }, ct);

            Assert.True(updated.Succeeded, updated.Error);
            var rows = await host.Config.GetOverridesAsync(instanceId, ct);
            var row = Assert.Single(rows);
            Assert.Equal(inserted.Value.Id, row.Id);
            Assert.Equal((IniFile.Game, "/Script/ShooterGame.ShooterGameMode", "TamingSpeedMultiplier", "3"), (row.File, row.Section, row.Key, row.Value));
        }
    }

    [Fact]
    public async Task SaveOverride_RejectsReservedKeysBadShapesUnknownInstancesAndDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, instanceId) = await StartAsync(ct);
        using (host)
        {
            Assert.True((await host.Config.SaveOverrideAsync(new ExtraOverride { InstanceId = instanceId, File = IniFile.GameUserSettings, Section = "ServerSettings", Key = "XPMultiplier", Value = "2" }, ct)).Succeeded);

            var reserved = await host.Config.SaveOverrideAsync(new ExtraOverride { InstanceId = instanceId, Section = "SessionSettings", Key = "SessionName", Value = "x" }, ct);
            var shape = await host.Config.SaveOverrideAsync(new ExtraOverride { InstanceId = instanceId, Section = "[Bad]", Key = "a=b", Value = "x" }, ct);
            var unknown = await host.Config.SaveOverrideAsync(new ExtraOverride { InstanceId = 999, Section = "ServerSettings", Key = "Other", Value = "x" }, ct);
            var duplicate = await host.Config.SaveOverrideAsync(new ExtraOverride { InstanceId = instanceId, File = IniFile.GameUserSettings, Section = "ServerSettings", Key = "XPMultiplier", Value = "9" }, ct);
            var otherFile = await host.Config.SaveOverrideAsync(new ExtraOverride { InstanceId = instanceId, File = IniFile.Game, Section = "ServerSettings", Key = "XPMultiplier", Value = "9" }, ct);

            Assert.Contains(reserved.Errors, e => e.Contains("reserved", StringComparison.Ordinal));
            Assert.True(shape.Errors.Count >= 2);
            Assert.Equal("The instance no longer exists.", unknown.Error);
            Assert.Equal("An override for [ServerSettings] XPMultiplier in GameUserSettings.ini already exists; edit that one instead.", duplicate.Error);
            Assert.True(otherFile.Succeeded, otherFile.Error);
            Assert.Equal(2, (await host.Config.GetOverridesAsync(instanceId, ct)).Count);
        }
    }

    [Fact]
    public async Task GetOverrides_OrdersByFileSectionKey_AndDeleteIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, instanceId) = await StartAsync(ct);
        using (host)
        {
            var b = await host.Config.SaveOverrideAsync(new ExtraOverride { InstanceId = instanceId, File = IniFile.GameUserSettings, Section = "ServerSettings", Key = "B", Value = "1" }, ct);
            var a = await host.Config.SaveOverrideAsync(new ExtraOverride { InstanceId = instanceId, File = IniFile.GameUserSettings, Section = "ServerSettings", Key = "A", Value = "1" }, ct);
            var game = await host.Config.SaveOverrideAsync(new ExtraOverride { InstanceId = instanceId, File = IniFile.Game, Section = "Z", Key = "Z", Value = "1" }, ct);

            var ordered = await host.Config.GetOverridesAsync(instanceId, ct);
            var deleted = await host.Config.DeleteOverrideAsync(b.Value!.Id, ct);
            var again = await host.Config.DeleteOverrideAsync(b.Value.Id, ct);

            Assert.Equal([game.Value!.Id, a.Value!.Id, b.Value.Id], ordered.Select(o => o.Id));
            Assert.True(deleted.Succeeded);
            Assert.True(again.Succeeded);
            Assert.Equal([game.Value.Id, a.Value.Id], (await host.Config.GetOverridesAsync(instanceId, ct)).Select(o => o.Id));
        }
    }
}
