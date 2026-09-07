namespace ArkAscendedServerAdmin.Ini;

/// <summary>
/// The "game defaults" starting texts offered when a cluster or standalone instance is created (DESIGN §5,
/// plan step 16). Deliberately minimal: the game materializes every default itself on first launch, and
/// the manager-owned keys (<see cref="Launch.ReservedKeys.IniKeys"/>) are left out so the editor never
/// warns about a fresh file. Only <c>ServerAdminPassword</c> is present, empty, because Start is refused
/// until it is set (plan step 23).
/// </summary>
public static class IniTemplates
{
    public const string DefaultGameUserSettings =
        "[ServerSettings]\r\n" +
        "; ServerAdminPassword is required: RCON is the only way the manager saves and stops the server.\r\n" +
        "; Set it before the first start.\r\n" +
        "ServerAdminPassword=\r\n" +
        "; ServerPassword=\r\n" +
        "; DifficultyOffset=1.0\r\n" +
        "; OverrideOfficialDifficulty=5.0\r\n" +
        "; XPMultiplier=1.0\r\n" +
        "; TamingSpeedMultiplier=1.0\r\n" +
        "; HarvestAmountMultiplier=1.0\r\n" +
        "; AllowThirdPersonPlayer=True\r\n" +
        "; ServerPVE=False\r\n" +
        "\r\n" +
        "[SessionSettings]\r\n" +
        "; The session name and ports are written here by the manager from the instance settings.\r\n" +
        "\r\n" +
        "[/Script/Engine.GameSession]\r\n" +
        "; The player limit is written here by the manager from the instance settings.\r\n";

    public const string DefaultGameIni =
        "[/Script/ShooterGame.ShooterGameMode]\r\n" +
        "; Gameplay rules live here: rates, engram and level overrides, dino tuning, structure limits.\r\n" +
        "; bDisableStructurePlacementCollision=False\r\n" +
        "; MatingIntervalMultiplier=1.0\r\n" +
        "; EggHatchSpeedMultiplier=1.0\r\n" +
        "; BabyMatureSpeedMultiplier=1.0\r\n";
}
