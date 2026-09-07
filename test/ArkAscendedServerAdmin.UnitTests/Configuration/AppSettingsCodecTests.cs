using ArkAscendedServerAdmin.Configuration;

namespace ArkAscendedServerAdmin.UnitTests.Configuration;

public class AppSettingsCodecTests
{
    [Fact]
    public void Encode_ProducesEveryKey()
    {
        var rows = AppSettingsCodec.Encode(new AppSettings());

        Assert.Equal(AppSettingsCodec.Keys.All.Order(), rows.Keys.Order());
    }

    [Fact]
    public void RoundTrip_PreservesEveryValue()
    {
        var original = new AppSettings
        {
            StaggerDelaySeconds = 45,
            SteamCmdValidate = true,
            GamePortStart = 7800,
            GamePortStep = 4,
            RconPortStart = 28000,
            RconPortStep = 2,
            DefaultBackupIntervalMinutes = 15,
            DefaultBackupRetention = 3,
            BackupQuiescenceSeconds = 20,
            PreStopBroadcastMinutes = 5,
            GracefulStopTimeoutSeconds = 90,
            RconCommandTimeoutSeconds = 5,
            ConsoleBackfillLines = 500,
            CurseForgeApiKey = "$2a$10$abc",
        };

        var decoded = AppSettingsCodec.Decode(AppSettingsCodec.Encode(original));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_EmptyRows_YieldsDefaults()
    {
        var decoded = AppSettingsCodec.Decode(new Dictionary<string, string>());

        Assert.Equal(new AppSettings(), decoded);
    }

    [Fact]
    public void Decode_IgnoresUnknownKeysAndUnparseableValues()
    {
        var rows = new Dictionary<string, string>
        {
            [AppSettingsCodec.Keys.GamePortStart] = "not-a-number",
            [AppSettingsCodec.Keys.SteamCmdValidate] = "maybe",
            ["SomethingFromTheFuture"] = "42",
            [AppSettingsCodec.Keys.RconPortStart] = "27100",
        };

        var decoded = AppSettingsCodec.Decode(rows);

        Assert.Equal(new AppSettings().GamePortStart, decoded.GamePortStart);
        Assert.False(decoded.SteamCmdValidate);
        Assert.Equal(27100, decoded.RconPortStart);
    }

    [Fact]
    public void Defaults_MatchThePlan()
    {
        var defaults = new AppSettings();

        Assert.Equal(30, defaults.StaggerDelaySeconds);
        Assert.False(defaults.SteamCmdValidate);
        Assert.Equal(7777, defaults.GamePortStart);
        Assert.Equal(2, defaults.GamePortStep);
        Assert.Equal(27020, defaults.RconPortStart);
        Assert.Equal(1, defaults.RconPortStep);
        Assert.Equal(30, defaults.DefaultBackupIntervalMinutes);
        Assert.Equal(10, defaults.DefaultBackupRetention);
        Assert.Equal(10, defaults.BackupQuiescenceSeconds);
        Assert.Equal(60, defaults.GracefulStopTimeoutSeconds);
        Assert.Equal(10, defaults.RconCommandTimeoutSeconds);
        Assert.Equal(200, defaults.ConsoleBackfillLines);
        Assert.Empty(defaults.Validate());
    }

    [Fact]
    public void Validate_ReportsOutOfRangeValues()
    {
        var settings = new AppSettings { GamePortStart = 0, RconPortStep = 0, CurseForgeApiKey = "has space" };

        var errors = settings.Validate();

        Assert.Contains(errors, e => e.StartsWith("GamePortStart", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("RconPortStep", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("CurseForgeApiKey", StringComparison.Ordinal));
        Assert.Equal(3, errors.Count);
    }
}
