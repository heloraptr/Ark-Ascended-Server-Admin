using System.Net;
using System.Text;

namespace ArkAscendedServerAdmin.UnitTests.CurseForge;

/// <summary>
/// Maps request paths (path + query) to canned JSON bodies and records what was requested.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    public StubHttpMessageHandler Respond(string pathAndQuery, string json)
    {
        _responses[pathAndQuery] = json;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri!.PathAndQuery;
        Requests.Add(key);

        if (!_responses.TryGetValue(key, out var json))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No stub for {key}"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }
}
