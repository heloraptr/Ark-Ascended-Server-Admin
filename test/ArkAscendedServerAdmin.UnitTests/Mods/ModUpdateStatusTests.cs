using ArkAscendedServerAdmin.Mods;

namespace ArkAscendedServerAdmin.UnitTests.Mods;

/// <summary>
/// The B8 decision: a mod counts as changed when its CurseForge date is later than the launch the manager
/// last issued, a never-launched instance never shows the badge, and an unknown date never counts.
/// </summary>
public class ModUpdateStatusTests
{
    private static readonly DateTimeOffset _launched = new(2026, 9, 18, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ModModifiedAfterTheLaunch_Changed()
    {
        Assert.True(ModUpdateStatus.ChangedSinceLaunch(_launched, [_launched.AddSeconds(1)]));
    }

    [Fact]
    public void ModModifiedBeforeOrExactlyAtTheLaunch_IsNotChanged()
    {
        Assert.False(ModUpdateStatus.ChangedSinceLaunch(_launched, [_launched, _launched.AddDays(-3)]));
    }

    [Fact]
    public void OneChangedModAmongUnchangedOnes_IsEnough()
    {
        Assert.True(ModUpdateStatus.ChangedSinceLaunch(_launched, [_launched.AddDays(-1), null, _launched.AddMinutes(5)]));
    }

    [Fact]
    public void UnknownDates_NeverCount()
    {
        Assert.False(ModUpdateStatus.ChangedSinceLaunch(_launched, [null, null]));
    }

    [Fact]
    public void NoMods_IsNotChanged()
    {
        Assert.False(ModUpdateStatus.ChangedSinceLaunch(_launched, []));
    }

    [Fact]
    public void InstanceThatWasNeverLaunched_IsNotChanged()
    {
        Assert.False(ModUpdateStatus.ChangedSinceLaunch(null, [_launched.AddYears(1)]));
    }

    [Fact]
    public void DifferentZones_CompareAsInstants()
    {
        var modified = new DateTimeOffset(2026, 9, 18, 17, 0, 1, TimeSpan.FromHours(-3)); // one second after the launch
        Assert.True(ModUpdateStatus.ChangedSinceLaunch(_launched, [modified]));
    }

    [Fact]
    public void LoadOverload_ReadsTheDatesOfTheModsTheInstanceLoads()
    {
        var dates = Dates((1, _launched.AddDays(-1)), (2, _launched.AddMinutes(1)));

        Assert.True(ModUpdateStatus.ChangedSinceLaunch(new InstanceModLoad(_launched, [1, 2]), dates));
        Assert.False(ModUpdateStatus.ChangedSinceLaunch(new InstanceModLoad(_launched, [1]), dates));
    }

    [Fact]
    public void LoadOverload_IgnoresModsTheLibraryDoesNotKnow()
    {
        Assert.False(ModUpdateStatus.ChangedSinceLaunch(new InstanceModLoad(_launched, [7]), Dates((1, _launched.AddDays(1)))));
    }

    [Fact]
    public void ChangedMods_ListsOnlyTheModsLoadedByAnInstanceLaunchedBeforeTheyChanged()
    {
        var dates = Dates((1, _launched.AddMinutes(1)), (2, _launched.AddMinutes(1)), (3, _launched.AddDays(-1)));
        var loads = new[]
        {
            new InstanceModLoad(_launched, [1, 3]),                  // 1 changed after its launch, 3 did not
            new InstanceModLoad(_launched.AddDays(1), [2]),          // launched after mod 2 changed
            new InstanceModLoad(null, [2]),                          // never launched
        };

        Assert.Equal([1], ModUpdateStatus.ChangedMods(loads, dates).Order());
    }

    [Fact]
    public void ChangedMods_CountsAModChangedForAnySingleInstanceThatLoadsIt()
    {
        var dates = Dates((1, _launched.AddMinutes(1)));
        var loads = new[] { new InstanceModLoad(_launched.AddDays(1), [1]), new InstanceModLoad(_launched, [1]) };

        Assert.Equal([1], ModUpdateStatus.ChangedMods(loads, dates).Order());
    }

    [Fact]
    public void ChangedMods_WithoutLoads_IsEmpty()
    {
        Assert.Empty(ModUpdateStatus.ChangedMods([], Dates((1, _launched.AddDays(1)))));
    }

    private static Dictionary<int, DateTimeOffset?> Dates(params (int Id, DateTimeOffset? Modified)[] entries) =>
        entries.ToDictionary(e => e.Id, e => e.Modified);
}
