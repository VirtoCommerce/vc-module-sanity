using System;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Pages.Core.Events;
using VirtoCommerce.Sanity.Data.Services;
using Xunit;

namespace VirtoCommerce.Sanity.Tests;

[Trait("Category", "Unit")]
public class SanityConverterTests
{
    private readonly SanityConverter _converter = new();

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"_type": "page"}""")]
    [InlineData("""{"_id": "release-1", "_type": "system.release"}""")]
    public void GetPageDocument_NoDocumentToIndex_ReturnsNull(string json)
    {
        var body = json == null ? null : JObject.Parse(json);

        Assert.Null(_converter.GetPageDocument(null, null, PageOperation.Publish, body, null));
    }

    [Theory]
    [InlineData("""{"_id": "page-1"}""")]
    [InlineData("""{"_id": "page-1", "_createdAt": null, "_updatedAt": null}""")]
    public void GetPageDocument_MissingOrNullDates_DefaultToNow(string json)
    {
        var before = DateTime.UtcNow;

        var pageDocument = _converter.GetPageDocument(null, null, PageOperation.Publish, JObject.Parse(json), null);

        Assert.Equal("page-1", pageDocument.Id);
        Assert.True(pageDocument.CreatedDate >= before);
        Assert.True(pageDocument.ModifiedDate >= before);
    }
}
