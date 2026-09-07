namespace ArkAscendedServerAdmin.CurseForge;

/// <summary>
/// Static CurseForge client settings. The API key is not here: it is an App Setting (database) and is
/// attached per request by the host's <c>x-api-key</c> handler.
/// </summary>
public class ApiOptions
{
    public string BaseUrl { get; set; } = "https://api.curseforge.com";
    public int ArkGameId { get; set; } = 83374;
}
