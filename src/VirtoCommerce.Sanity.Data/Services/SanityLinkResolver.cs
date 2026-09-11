using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Sanity.Core.Services;

namespace VirtoCommerce.Sanity.Data.Services;

/// <summary>
/// Enriches Sanity documents in place: every internal document reference gets a "slug" property
/// with the relative link (permalink) of the referenced document, and every asset reference
/// (image or file) gets a "url" property with its CDN URL.
/// </summary>
public class SanityLinkResolver(ISanityApiClient apiClient)
{
    // The GROQ query is sent as a GET URL; keep batches small enough to stay within Sanity's URL length limit
    private const int ReferenceBatchSize = 100;
    private const string SlugPropertyName = "slug";
    private const string SlugProjection = "coalesce(permalink.current, seo.slug.current, slug.current)";
    private const string UrlPropertyName = "url";
    private const string AssetCdnBaseUrl = "https://cdn.sanity.io";
    private const string ImageAssetPrefix = "image-";
    private const string FileAssetPrefix = "file-";

    public virtual async Task ResolveLinksAsync(string projectId, string dataset, string apiToken, IList<JObject> documents)
    {
        var references = documents.SelectMany(CollectReferences).ToList();

        ResolveAssetUrls(projectId, dataset, references.Where(IsAssetReference));

        await ResolveDocumentSlugsAsync(projectId, dataset, apiToken, references.Where(x => !IsAssetReference(x)).ToList());
    }

    protected virtual void ResolveAssetUrls(string projectId, string dataset, IEnumerable<JObject> assetReferences)
    {
        foreach (var reference in assetReferences)
        {
            var url = BuildAssetUrl(projectId, dataset, GetReferencedId(reference));
            if (url != null)
            {
                reference[UrlPropertyName] = url;
            }
        }
    }

    // Sanity asset ids encode everything needed for the CDN URL, so no API request is required:
    // image-<hash>-<width>x<height>-<format> -> https://cdn.sanity.io/images/<projectId>/<dataset>/<hash>-<width>x<height>.<format>
    // file-<hash>-<extension>                -> https://cdn.sanity.io/files/<projectId>/<dataset>/<hash>.<extension>
    private static string BuildAssetUrl(string projectId, string dataset, string assetId)
    {
        var kind = assetId.StartsWith(ImageAssetPrefix, StringComparison.Ordinal) ? "images" : "files";
        var name = assetId[(assetId.IndexOf('-') + 1)..];

        var extensionSeparator = name.LastIndexOf('-');
        if (extensionSeparator <= 0 || extensionSeparator == name.Length - 1)
        {
            // Malformed asset id without a format suffix; leave the reference untouched
            return null;
        }

        return $"{AssetCdnBaseUrl}/{kind}/{projectId}/{dataset}/{name[..extensionSeparator]}.{name[(extensionSeparator + 1)..]}";
    }

    protected virtual async Task ResolveDocumentSlugsAsync(string projectId, string dataset, string apiToken, IList<JObject> references)
    {
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

    private static IEnumerable<JObject> CollectReferences(JObject document)
    {
        return document
            .DescendantsAndSelf()
            .OfType<JObject>()
            .Where(IsReference);
    }

    private static bool IsReference(JObject candidate)
    {
        return candidate["_type"]?.ToString() == "reference" &&
               !string.IsNullOrEmpty(GetReferencedId(candidate));
    }

    private static bool IsAssetReference(JObject reference)
    {
        var referencedId = GetReferencedId(reference);

        return referencedId.StartsWith(ImageAssetPrefix, StringComparison.Ordinal) ||
               referencedId.StartsWith(FileAssetPrefix, StringComparison.Ordinal);
    }

    private static string GetReferencedId(JObject reference)
    {
        return reference["_ref"]?.ToString();
    }
}
