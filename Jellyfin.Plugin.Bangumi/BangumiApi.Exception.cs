using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Bangumi;

public partial class BangumiApi
{
    private static async Task HandleHttpException(HttpResponseMessage response, CancellationToken token = default)
    {
        var requestUri = response.RequestMessage?.RequestUri;
        var message = $"unknown response from {requestUri} {(int)response.StatusCode}: {response.ReasonPhrase}";
        try
        {
            var content = await response.Content.ReadAsStringAsync(token);
            message = $"unknown response from {requestUri} {(int)response.StatusCode}: {content}";
            var result = JsonSerializer.Deserialize<Response>(content, Constants.JsonSerializerOptions);
            if (result?.Title != null)
                message = $"{result.Title}: {result.Description}";
        }
        catch
        {
            // ignored
        }

        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private sealed class Response
    {
        public string Title { get; set; } = "";

        public string Description { get; set; } = "";
    }
}
