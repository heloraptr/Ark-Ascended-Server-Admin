using ArkAscendedServerAdmin.CurseForge.Models.Mods;

namespace ArkAscendedServerAdmin.UnitTests.CurseForge;

public class ModExtensionsTests
{
    [Fact]
    public void FromCurseForgeMod_MapsIdNameThumbnailAndCategories()
    {
        var mod = new Mod
        {
            Id = 42,
            Name = "Test Mod",
            Logo = new ModAsset { ThumbnailUrl = "https://img/42.png" },
            Categories =
            [
                new Category { Id = 1, Name = "Structures", IconUrl = "https://img/c1.png" },
                new Category { Id = 2, Name = "Creatures", IconUrl = "https://img/c2.png" },
            ],
        };

        var result = mod.FromCurseForgeMod();

        Assert.Equal(42, result.Id);
        Assert.Equal("Test Mod", result.Name);
        Assert.Equal("https://img/42.png", result.ImageUrl);
        Assert.Collection(
            result.Categories,
            c => Assert.Equal(("Structures", "https://img/c1.png"), (c.Name, c.ImageUrl)),
            c => Assert.Equal(("Creatures", "https://img/c2.png"), (c.Name, c.ImageUrl)));
    }
}
