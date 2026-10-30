using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

static class HttpSupport
{
    private static readonly HttpClient Client = new();

    public static async Task<HttpJsonResult> GetJsonAsync(string url, IReadOnlyDictionary<string, string> headers) =>
        await SendJsonAsync(HttpMethod.Get, url, headers, null);

    public static async Task<HttpJsonResult> PostJsonAsync(string url, IReadOnlyDictionary<string, string> headers, JsonNode body) =>
        await SendJsonAsync(HttpMethod.Post, url, headers, body);

    public static async Task<HttpJsonResult> PutJsonAsync(string url, IReadOnlyDictionary<string, string> headers, JsonNode body) =>
        await SendJsonAsync(HttpMethod.Put, url, headers, body);

    private static async Task<HttpJsonResult> SendJsonAsync(
        HttpMethod method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        JsonNode? body)
    {
        using var request = new HttpRequestMessage(method, url);
        foreach (var header in headers)
        {
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(JsonSupport.Compact), Encoding.UTF8, "application/json");
        }

        using var response = await Client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var error = string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content;
            return new HttpJsonResult(false, null, error, (int)response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return new HttpJsonResult(true, null, null, null);
        }

        return new HttpJsonResult(true, JsonNode.Parse(content), null, null);
    }
}


sealed record HttpJsonResult(bool Success, JsonNode? Json, string? ErrorMessage, int? StatusCode);

