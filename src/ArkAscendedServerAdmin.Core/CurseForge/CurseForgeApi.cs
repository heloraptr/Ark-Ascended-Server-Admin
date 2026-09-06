using System.Net.Http.Json;
using System.Text.Json;
using ArkAscendedServerAdmin.CurseForge.Models;
using ArkAscendedServerAdmin.CurseForge.Models.Mods;
using ArkAscendedServerAdmin.CurseForge.Models.Services;

namespace ArkAscendedServerAdmin.CurseForge;

/// <summary>
/// Thin HTTP client for the CurseForge v1 API. The <see cref="HttpClient"/> is expected to carry the
/// base address and the <c>x-api-key</c> header; this class only knows the endpoints.
/// </summary>
public class CurseForgeApi(HttpClient http, ApiOptions options) : ICurseForgeApi
{
    private const int PageSize = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<List<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = $"/v1/categories?gameId={options.ArkGameId}";
        using var res = await http.GetAsync(endpoint, cancellationToken);
        return (await GetListResponse<Category>(res, cancellationToken)).Data;
    }

    public async Task<List<Mod>> SearchModsAsync(string searchTerm, int? categoryId = null, CancellationToken cancellationToken = default)
    {
        var results = new List<Mod>();
        var encodedSearchTerm = Uri.EscapeDataString(searchTerm);
        var baseEndpoint = $"/v1/mods/search?gameId={options.ArkGameId}&searchFilter={encodedSearchTerm}";
        if (categoryId.HasValue)
        {
            baseEndpoint += $"&categoryId={categoryId.Value}";
        }

        var page = 0;
        int totalCount;

        do
        {
            var endpoint = baseEndpoint;
            if (page > 0)
            {
                endpoint += $"&index={page * PageSize}";
            }

            using var res = await http.GetAsync(endpoint, cancellationToken);
            var response = await GetListResponse<Mod>(res, cancellationToken);
            results.AddRange(response.Data);

            totalCount = response.Pagination?.TotalCount ?? results.Count;
            page++;
        }
        while (results.Count < totalCount);

        return results;
    }

    public async Task<List<Mod>> GetModsAsync(IEnumerable<int> modIds, bool pcOnly = true, CancellationToken cancellationToken = default)
    {
        var request = new ModsByIdsApiRequest { ModIds = modIds, FilterPCOnly = pcOnly };
        using var content = JsonContent.Create(request);

        using var res = await http.PostAsync("/v1/mods", content, cancellationToken);
        var data = await GetListResponse<Mod>(res, cancellationToken);
        return data.Data;
    }

    public async Task<Mod> GetModAsync(int id, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/v1/mods/{id}";
        using var res = await http.GetAsync(endpoint, cancellationToken);
        return (await GetItemResponse<Mod>(res, cancellationToken)).Data;
    }

    private static async Task<ApiListResponse<T>> GetListResponse<T>(HttpResponseMessage res, CancellationToken cancellationToken)
    {
        res.EnsureSuccessStatusCode();
        var content = await res.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<ApiListResponse<T>>(content, JsonOptions)
               ?? throw new InvalidOperationException("CurseForge returned an empty list response.");
    }

    private static async Task<ApiItemResponse<T>> GetItemResponse<T>(HttpResponseMessage res, CancellationToken cancellationToken)
    {
        res.EnsureSuccessStatusCode();
        var content = await res.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<ApiItemResponse<T>>(content, JsonOptions)
               ?? throw new InvalidOperationException("CurseForge returned an empty item response.");
    }
}
