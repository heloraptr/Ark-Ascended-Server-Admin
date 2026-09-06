namespace ArkAscendedServerAdmin.CurseForge;

public class ApiOptions
{
    public string BaseUrl { get; set; } = "https://api.curseforge.com";
    public string ApiKey { get; set; } = string.Empty;
    public int ArkGameId { get; set; } = 83374;
}