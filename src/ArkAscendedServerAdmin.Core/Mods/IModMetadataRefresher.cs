using ArkAscendedServerAdmin.Commands;

namespace ArkAscendedServerAdmin.Mods;

/// <summary>
/// Re-fetches CurseForge metadata for every library entry (B8). The command facade calls this after the
/// guard, and the daily poll calls it with no user at all, so the refresh itself never checks
/// authorization.
/// </summary>
public interface IModMetadataRefresher
{
    /// <summary>
    /// Updates name, summary, thumbnail, page link, and <c>dateModified</c> for every entry and returns how many rows
    /// changed. Fails with <see cref="ModMetadata.NoApiKeyMessage"/> when no CurseForge key is configured,
    /// and with a readable sentence when the API cannot be reached.
    /// </summary>
    Task<CommandResult<int>> RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>Text shared by the mod command facade and the refresher.</summary>
public static class ModMetadata
{
    public const string NoApiKeyMessage = "Add a CurseForge API key on the Settings page to search. Mods can still be added by id.";

    /// <summary>What the page says when a CurseForge call fails: a rejected key reads differently from an unreachable API.</summary>
    public static string DescribeApiFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized }
            ? "CurseForge rejected the API key. Check it on the Settings page."
            : $"CurseForge could not be reached: {failure.Message}";
    }
}
