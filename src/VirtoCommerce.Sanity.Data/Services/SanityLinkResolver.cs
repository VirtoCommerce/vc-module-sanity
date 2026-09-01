using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Sanity.Core.Services;

namespace VirtoCommerce.Sanity.Data.Services;

/// <summary>
/// Enriches Sanity documents in place: every internal document reference gets a "slug" property
/// with the relative link (permalink) of the referenced document.
/// </summary>
public class SanityLinkResolver(ISanityApiClient apiClient)
{
    // The GROQ query is sent as a GET URL; keep batches small enough to stay within Sanity's URL length limit
    private const int ReferenceBatchSize = 100;
    private const string SlugPropertyName = "slug";
    private const string SlugProjection = "coalesce(permalink.current, seo.slug.current, slug.current)";

    public virtual async Task ResolveLinksAsync(string projectId, string dataset, string apiToken, IList<JObject> documents)
    {
        var references = documents.SelectMany(CollectDocumentReferences).ToList();
        if (references.Count == 0)
        {
            return;
        }

        var referencedIds = references
            .Select(GetReferencedId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var slugsByDocumentId = await GetSlugsByDocumentIdAsync(projectId, dataset, apiToken, referencedIds);

        foreach (var reference in references)
        {
            if (slugsByDocumentId.TryGetValue(GetReferencedId(reference), out var slug))
            {
                reference[SlugPropertyName] = slug;
            }
        }
    }

    protected virtual async Task<IDictionary<string, string>> GetSlugsByDocumentIdAsync(
        string projectId, string dataset, string apiToken, IList<string> documentIds)
    {
        var slugsByDocumentId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var batch in documentIds.Chunk(ReferenceBatchSize))
        {
            var idsFilter = string.Join(", ", batch.Select(id => $"\"{id}\""));
            var query = $"*[_id in [{idsFilter}]]{{_id, \"{SlugPropertyName}\": {SlugProjection}}}";
            var response = await apiClient.QueryAsync(projectId, dataset, apiToken, query);

            foreach (var document in response.Results)
            {
                var id = document["_id"]?.ToString();
                var slug = document[SlugPropertyName]?.ToString();

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(slug))
                {
                    slugsByDocumentId[id] = slug;
                }
            }
        }

        return slugsByDocumentId;
    }

    private static IEnumerable<JObject> CollectDocumentReferences(JObject document)
    {
        return document
            .DescendantsAndSelf()
            .OfType<JObject>()
            .Where(IsDocumentReference);
    }

    private static bool IsDocumentReference(JObject candidate)
    {
        if (candidate["_type"]?.ToString() != "reference")
        {
            return false;
        }

        var referencedId = GetReferencedId(candidate);

        // Asset references (images, files) have no documents with permalinks behind them
        return !string.IsNullOrEmpty(referencedId) &&
               !referencedId.StartsWith("image-", StringComparison.Ordinal) &&
               !referencedId.StartsWith("file-", StringComparison.Ordinal);
    }

    private static string GetReferencedId(JObject reference)
    {
        return reference["_ref"]?.ToString();
    }
}
