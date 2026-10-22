using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace VirtoCommerce.Sanity.Core.Services;

/// <summary>
/// Enriches Sanity documents in place before they are indexed: internal document references get
/// the slug of the referenced document, asset references get their CDN URL.
/// </summary>
public interface ISanityLinkResolver
{
    Task ResolveLinksAsync(string projectId, string dataset, string apiToken, IList<JObject> documents);
}
