using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Sanity.Core.Services;
using VirtoCommerce.Sanity.Data.Services;
using Xunit;

namespace VirtoCommerce.Sanity.Tests;

[Trait("Category", "Unit")]
public class SanityLinkResolverTests
{
    [Fact]
    public async Task ResolveLinksAsync_AddsSlugToNestedReferences()
    {
        var apiClient = new FakeSanityApiClient
        {
            Results =
            [
                JObject.Parse("""{"_id": "page-about", "slug": "about-us"}"""),
                JObject.Parse("""{"_id": "page-contacts", "slug": "contacts"}"""),
            ],
        };
        var resolver = new SanityLinkResolver(apiClient);

        var document = JObject.Parse("""
        {
            "_id": "footer",
            "_type": "footerNavigation",
            "columns": [
                {
                    "links": [
                        { "link": { "label": "About", "internalLink": { "_type": "reference", "_ref": "page-about" } } },
                        { "link": { "label": "External", "externalUrl": "https://example.com" } }
                    ]
                }
            ],
            "legalLinks": [
                { "internalLink": { "_type": "reference", "_ref": "page-contacts" } }
            ]
        }
        """);

        await resolver.ResolveLinksAsync("project", "production", "token", [document]);

        Assert.Equal("about-us", document.SelectToken("columns[0].links[0].link.internalLink.slug")?.ToString());
        Assert.Equal("contacts", document.SelectToken("legalLinks[0].internalLink.slug")?.ToString());

        var query = Assert.Single(apiClient.Queries);
        Assert.Contains("\"page-about\"", query);
        Assert.Contains("\"page-contacts\"", query);
        Assert.Contains("coalesce(permalink.current, seo.slug.current, slug.current)", query);
    }

    [Fact]
    public async Task ResolveLinksAsync_AddsCdnUrlToAssetReferencesWithoutQueryingApi()
    {
        var apiClient = new FakeSanityApiClient();
        var resolver = new SanityLinkResolver(apiClient);

        var document = JObject.Parse("""
        {
            "_id": "page-1",
            "_type": "page",
            "image": { "asset": { "_type": "reference", "_ref": "image-abc123-800x600-jpg" } },
            "attachment": { "asset": { "_type": "reference", "_ref": "file-def456-pdf" } }
        }
        """);

        await resolver.ResolveLinksAsync("project", "production", "token", [document]);

        Assert.Equal(
            "https://cdn.sanity.io/images/project/production/abc123-800x600.jpg",
            document.SelectToken("image.asset.url")?.ToString());
        Assert.Equal(
            "https://cdn.sanity.io/files/project/production/def456.pdf",
            document.SelectToken("attachment.asset.url")?.ToString());

        // Asset URLs are built from the asset id; the Sanity API must not be queried
        Assert.Empty(apiClient.Queries);
    }

    [Fact]
    public async Task ResolveLinksAsync_MixedReferences_ResolvesSlugsAndAssetUrlsIndependently()
    {
        var apiClient = new FakeSanityApiClient
        {
            Results = [JObject.Parse("""{"_id": "page-about", "slug": "about-us"}""")],
        };
        var resolver = new SanityLinkResolver(apiClient);

        var document = JObject.Parse("""
        {
            "_id": "footer",
            "_type": "footerNavigation",
            "link": { "internalLink": { "_type": "reference", "_ref": "page-about" } },
            "logo": { "asset": { "_type": "reference", "_ref": "image-fff000-100x50-svg" } }
        }
        """);

        await resolver.ResolveLinksAsync("project", "production", "token", [document]);

        Assert.Equal("about-us", document.SelectToken("link.internalLink.slug")?.ToString());
        Assert.Equal(
            "https://cdn.sanity.io/images/project/production/fff000-100x50.svg",
            document.SelectToken("logo.asset.url")?.ToString());

        // The single API query is for the document slug; the asset id must not leak into it
        var query = Assert.Single(apiClient.Queries);
        Assert.Contains("\"page-about\"", query);
        Assert.DoesNotContain("image-fff000", query);
    }

    [Fact]
    public async Task ResolveLinksAsync_MalformedAssetId_LeavesReferenceUntouched()
    {
        var apiClient = new FakeSanityApiClient();
        var resolver = new SanityLinkResolver(apiClient);

        var document = JObject.Parse("""
        {
            "_id": "page-1",
            "_type": "page",
            "image": { "asset": { "_type": "reference", "_ref": "image-noformat" } }
        }
        """);

        await resolver.ResolveLinksAsync("project", "production", "token", [document]);

        Assert.Null(document.SelectToken("image.asset.url"));
        Assert.Empty(apiClient.Queries);
    }

    [Fact]
    public async Task ResolveLinksAsync_LeavesUnresolvedReferencesUntouched()
    {
        var apiClient = new FakeSanityApiClient
        {
            Results = [JObject.Parse("""{"_id": "page-known", "slug": "known"}""")],
        };
        var resolver = new SanityLinkResolver(apiClient);

        var document = JObject.Parse("""
        {
            "_id": "doc-1",
            "_type": "page",
            "known": { "_type": "reference", "_ref": "page-known" },
            "unknown": { "_type": "reference", "_ref": "page-unknown" }
        }
        """);

        await resolver.ResolveLinksAsync("project", "production", "token", [document]);

        Assert.Equal("known", document.SelectToken("known.slug")?.ToString());
        Assert.Null(document.SelectToken("unknown.slug"));
    }

    [Fact]
    public async Task ResolveLinksAsync_NoReferences_DoesNotQuery()
    {
        var apiClient = new FakeSanityApiClient();
        var resolver = new SanityLinkResolver(apiClient);

        var document = JObject.Parse("""{"_id": "doc-1", "_type": "page", "title": "Plain"}""");

        await resolver.ResolveLinksAsync("project", "production", "token", [document]);

        Assert.Empty(apiClient.Queries);
    }

    private sealed class FakeSanityApiClient : ISanityApiClient
    {
        public List<string> Queries { get; } = [];
        public List<JObject> Results { get; init; } = [];

        public Task<SanityQueryResponse> QueryAsync(string projectId, string dataset, string apiToken, string groqQuery)
        {
            Queries.Add(groqQuery);
            return Task.FromResult(new SanityQueryResponse
            {
                Results = Results,
                TotalCount = Results.Count,
            });
        }
    }
}
