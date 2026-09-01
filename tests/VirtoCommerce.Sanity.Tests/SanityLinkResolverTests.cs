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
    public async Task ResolveLinksAsync_SkipsAssetReferences()
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
