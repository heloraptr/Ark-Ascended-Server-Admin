using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.UnitTests.Maps;

public class MapOrderingTests
{
    [Fact]
    public void InDisplayOrder_GroupsByType_ThenReleaseDateWithUnknownLast_ThenName()
    {
        Map[] maps =
        [
            new() { Key = "custom-b", Name = "Beta", IsOfficial = false },
            new() { Key = "nc-late", Name = "Late NC", IsOfficial = true, ReleaseDate = new DateOnly(2025, 6, 1) },
            new() { Key = "custom-dated", Name = "Zulu", IsOfficial = false, ReleaseDate = new DateOnly(2024, 1, 1) },
            new() { Key = "story-late", Name = "Late Story", IsOfficial = true, IsStory = true, ReleaseDate = new DateOnly(2025, 1, 1) },
            new() { Key = "custom-a", Name = "alpha", IsOfficial = false },
            new() { Key = "nc-early", Name = "Early NC", IsOfficial = true, ReleaseDate = new DateOnly(2024, 6, 1) },
            new() { Key = "story-early", Name = "Early Story", IsOfficial = true, IsStory = true, ReleaseDate = new DateOnly(2023, 1, 1) },
            new() { Key = "nc-undated", Name = "Undated NC", IsOfficial = true },
        ];

        var ordered = maps.InDisplayOrder().Select(m => m.Key).ToList();

        Assert.Equal(["story-early", "story-late", "nc-early", "nc-late", "nc-undated", "custom-dated", "custom-a", "custom-b"], ordered);
    }

    [Fact]
    public void TypeLabel_FollowsTheTwoFlags()
    {
        Assert.Equal("Official - Story", new Map { Key = "k", Name = "n", IsOfficial = true, IsStory = true }.TypeLabel);
        Assert.Equal("Official - Non-Canon", new Map { Key = "k", Name = "n", IsOfficial = true }.TypeLabel);
        Assert.Equal("Custom/Mod", new Map { Key = "k", Name = "n", IsStory = true }.TypeLabel);
    }
}
