using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace VirtoCommerce.Sanity.Core.Services;

public interface ISanityApiClient
{
    Task<SanityQueryResponse> QueryAsync(string projectId, string dataset, string apiToken, string groqQuery);
}

public class SanityQueryResponse
{
    public IList<JObject> Results { get; set; } = [];
    public int TotalCount { get; set; }
}
