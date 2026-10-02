using System.Net.Http.Json;
using System.Text.Json;
using ArkAscendedServerAdmin.CurseForge.Models;
using ArkAscendedServerAdmin.CurseForge.Models.Mods;

namespace ArkAscendedServerAdmin.CurseForge;

/// <summary>
/// Thin HTTP client for the CurseForge v1 API. The <see cref="HttpClient"/> is expected to carry the
/// base address and the <c>x-api-key</c> header; this class only knows the endpoints.
/// </summary>
public class CurseForgeApi(HttpClient http, ApiOptions options) : ICurseForgeApi
{
    private const int PageSize = 50;

    /// <summary>
    /// The most results one search returns: two pages. A broad term can match thousands of mods, and walking
    /// them all is many slow requests that CurseForge refuses past 10,000 anyway; a narrower term is the better fix.
    /// </summary>
    public const int MaxSearchResults = 2 * PageSize;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<List<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = $"/v1/categories?gameId={options.ArkGameId}";
        using var res = await http.GetAsync(endpoint, cancellationToken);
        return (await GetListResponseAsync<Category>(res, cancellationToken)).Data;
    }

    public async Task<ModSearchResult> SearchModsAsync(string searchTerm, int? categoryId = null, CancellationToken cancellationToken = default)
    {
        var results = new List<Mod>();
        var encodedSearchTerm = Uri.EscapeDataString(searchTerm);
        var baseEndpoint = $"/v1/mods/search?gameId={options.ArkGameId}&searchFilter={encodedSearchTerm}";
        if (categoryId.HasValue)
        {
            baseEndpoint += $"&categoryId={categoryId.Value}";
        }

        var totalCount = 0;
        while (results.Count < MaxSearchResults)
        {
            var endpoint = baseEndpoint;
            if (results.Count > 0)
            {
                endpoint += $"&index={results.Count}";
            }

            using var res = await http.GetAsync(endpoint, cancellationToken);
            var response = await GetListResponseAsync<Mod>(res, cancellationToken);
            results.AddRange(response.Data);
            totalCount = response.Pagination?.TotalCount ?? results.Count;

            // An empty or short page means CurseForge has nothing further to give, whatever its total says.
            if (response.Data.Count < PageSize || results.Count >= totalCount)
            {
                break;
            }
        }

        if (results.Count > MaxSearchResults)
        {
            results.RemoveRange(MaxSearchResults, results.Count - MaxSearchResults);
        }

        return new ModSearchResult(results, totalCount);
    }

    public async Task<List<Mod>> GetModsAsync(IEnumerable<int> modIds, bool pcOnly = true, CancellationToken cancellationToken = default)
    {
        var request = new ModsByIdsApiRequest { ModIds = modIds, FilterPCOnly = pcOnly };
        using var content = JsonContent.Create(request);

        using var res = await http.PostAsync("/v1/mods", content, cancellationToken);
        var data = await GetListResponseAsync<Mod>(res, cancellationToken);
        return data.Data;
    }

    public async Task<Mod> GetModAsync(int id, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/v1/mods/{id}";
        using var res = await http.GetAsync(endpoint, cancellationToken);
        return (await GetItemResponseAsync<Mod>(res, cancellationToken)).Data;
    }

    private static async Task<ApiListResponse<T>> GetListResponseAsync<T>(HttpResponseMessage res, CancellationToken cancellationToken)
    {
        res.EnsureSuccessStatusCode();
        var content = await res.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<ApiListResponse<T>>(content, _jsonOptions)
               ?? throw new InvalidOperationException("CurseForge returned an empty list response.");
    }

    private static async Task<ApiItemResponse<T>> GetItemResponseAsync<T>(HttpResponseMessage res, CancellationToken cancellationToken)
    {
        res.EnsureSuccessStatusCode();
        var content = await res.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<ApiItemResponse<T>>(content, _jsonOptions)
               ?? throw new InvalidOperationException("CurseForge returned an empty item response.");
    }
}
