using ArkAscendedServerAdmin.Configuration;

namespace ArkAscendedServerAdmin.Infrastructure.CurseForge;

/// <summary>
/// Attaches the CurseForge <c>x-api-key</c> header from App Settings on every request, so a key edited on
/// the Settings page takes effect without a restart. With no key configured the request goes out
/// unauthenticated and the API answers 403; the mod library then falls back to manual id entry.
/// </summary>
public sealed class CurseForgeApiKeyHandler(IAppSettingsStore settings) : DelegatingHandler
{
    private const string HeaderName = "x-api-key";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = (await settings.GetAsync(cancellationToken)).CurseForgeApiKey;
        request.Headers.Remove(HeaderName);
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.TryAddWithoutValidation(HeaderName, key);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
