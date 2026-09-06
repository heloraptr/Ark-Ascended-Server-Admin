using System.Text.Json.Serialization;

namespace ArkAscendedServerAdmin.CurseForge.Models;

public class ModsByIdsApiRequest
{
    [JsonPropertyName("modIds")] public IEnumerable<int> ModIds { get; set; } = [];
    [JsonPropertyName("filterPcOnly")] public bool FilterPCOnly { get; set; } = true;
}