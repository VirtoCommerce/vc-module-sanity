using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Sanity.Core.Services;

namespace VirtoCommerce.Sanity.Data.Services;

public class SanityApiClient(IHttpClientFactory httpClientFactory) : ISanityApiClient
{
    private const string ApiVersion = "v2021-10-21";

    public async Task<SanityQueryResponse> QueryAsync(string projectId, string dataset, string apiToken, string groqQuery)
    {
        var url = $"https://{Uri.EscapeDataString(projectId)}.api.sanity.io/{ApiVersion}/data/query/{Uri.EscapeDataString(dataset)}?query={Uri.EscapeDataString(groqQuery)}";

        var client = httpClientFactory.CreateClient("Sanity");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

        try
        {
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = JObject.Parse(await response.Content.ReadAsStringAsync());
            var results = json["result"]?.ToObject<List<JObject>>() ?? [];

            return new SanityQueryResponse
            {
                Results = results,
                TotalCount = results.Count,
            };
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(
                $"Sanity API query failed for project '{projectId}', dataset '{dataset}', query '{groqQuery}'.",
                ex);
        }
    }
}
