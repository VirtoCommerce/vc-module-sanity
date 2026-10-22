using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Pages.Core.Events;
using VirtoCommerce.Pages.Core.Models;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Sanity.Core.Services;
using VirtoCommerce.Sanity.Data.Services;
using VirtoCommerce.Sanity.Web.Controllers.Api;
using VirtoCommerce.Sanity.Web.Filters;
using Xunit;

namespace VirtoCommerce.Sanity.Tests;

[Trait("Category", "Unit")]
public class SanityControllerTests
{
    private readonly Mock<ISanityContentProvider> _contentProvider = new();
    private readonly List<PagesDomainEvent> _publishedEvents = [];

    public SanityControllerTests()
    {
        _contentProvider
            .Setup(x => x.GetByIdsAsync(It.IsAny<IList<string>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<string> ids, string _, string _, CancellationToken _) => ids.Select(id => new PageDocument { Id = id, StoreId = "store1" }).ToList());
        _contentProvider
            .Setup(x => x.GetStoreIdsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    [Fact]
    public async Task Post_HeadersOnly_DocumentIsFetchedFromHeaderSource()
    {
        var result = await CreateController().Post(null, "update", "projA", "production", "page-1", []);

        Assert.IsType<OkResult>(result);
        _contentProvider.Verify(x => x.GetByIdsAsync(It.Is<IList<string>>(ids => ids.Single() == "page-1"), "projA", "production", It.IsAny<CancellationToken>()));
        Assert.Equal("page-1", Assert.Single(_publishedEvents).Page.Id);
    }

    [Fact]
    public async Task Post_SignedBodyCarriesValues_HeadersAreNotNeeded()
    {
        var body = JObject.Parse("""{"_id": "page-1", "_operation": "update", "_projectId": "projA", "_dataset": "production"}""");

        var result = await CreateController().Post(null, null, null, null, null, body);

        Assert.IsType<OkResult>(result);
        _contentProvider.Verify(x => x.GetByIdsAsync(It.Is<IList<string>>(ids => ids.Single() == "page-1"), "projA", "production", It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData("delete", "page-1", "projA", "production")]
    [InlineData("update", "page-2", "projA", "production")]
    [InlineData("update", "page-1", "projB", "production")]
    [InlineData("update", "page-1", "projA", "staging")]
    public async Task Post_HeaderContradictsSignedBody_RequestIsRejected(string operation, string documentId, string projectId, string dataset)
    {
        var body = JObject.Parse("""{"_id": "page-1", "_operation": "update", "_projectId": "projA", "_dataset": "production"}""");

        var result = await CreateController().Post(null, operation, projectId, dataset, documentId, body);

        Assert.IsType<BadRequestResult>(result);
        Assert.Empty(_publishedEvents);
        _contentProvider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Post_FullDocumentProjection_DocumentFieldsNamedLikeHeadersAreIgnored()
    {
        // A projection shipping the whole document: its own "operation", "projectId" and "dataset" fields
        // are content, not routing values, and must not contradict the headers
        var body = JObject.Parse("""{"_id": "page-1", "operation": "promo", "projectId": "other", "dataset": "other", "title": "Page"}""");

        var result = await CreateController().Post(null, "update", "projA", "production", "page-1", body);

        Assert.IsType<OkResult>(result);
        _contentProvider.Verify(x => x.GetByIdsAsync(It.Is<IList<string>>(ids => ids.Single() == "page-1"), "projA", "production", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void Post_IsAnonymousAndAuthenticatedBySignature()
    {
        var method = typeof(SanityController).GetMethod(nameof(SanityController.Post));

        Assert.NotNull(method.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(method.GetCustomAttribute<SanityWebhookSignatureAttribute>());
    }

    [Fact]
    public async Task Post_DocumentIdWithGroqSyntax_RequestIsRejected()
    {
        var result = await CreateController().Post(null, "update", "projA", "production", "x\"] || _type == \"secret", []);

        Assert.IsType<BadRequestResult>(result);
        _contentProvider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Post_DeleteWithoutStoreInPayload_EventIsPublishedForEveryStoreOfTheSource()
    {
        _contentProvider
            .Setup(x => x.GetStoreIdsAsync("projA", "production", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["store1", "store2"]);

        var result = await CreateController().Post(null, "delete", "projA", "production", "page-1", []);

        Assert.IsType<OkResult>(result);
        Assert.Equal(["store1", "store2"], _publishedEvents.Select(x => x.Page.StoreId).ToArray());
        Assert.All(_publishedEvents, x =>
        {
            Assert.Equal("page-1", x.Page.Id);
            Assert.Equal(PageOperation.Delete, x.Operation);
        });
    }

    [Fact]
    public async Task Post_DeleteWithStoreInPayload_StoreIsNotResolved()
    {
        var body = JObject.Parse("""{"_id": "page-1", "storeId": "store3"}""");

        await CreateController().Post(null, "delete", "projA", "production", null, body);

        Assert.Equal("store3", Assert.Single(_publishedEvents).Page.StoreId);
        _contentProvider.Verify(x => x.GetStoreIdsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Post_DeleteOfUnknownSource_EventIsPublishedWithoutStore()
    {
        await CreateController().Post(null, "delete", "projX", "production", "page-1", []);

        var pageEvent = Assert.Single(_publishedEvents);
        Assert.Equal("page-1", pageEvent.Page.Id);
        Assert.Null(pageEvent.Page.StoreId);
    }

    private SanityController CreateController()
    {
        var eventPublisher = new Mock<IEventPublisher>();
        eventPublisher
            .Setup(x => x.Publish(It.IsAny<PagesDomainEvent>(), It.IsAny<CancellationToken>()))
            .Callback((PagesDomainEvent message, CancellationToken _) => _publishedEvents.Add(message))
            .Returns(Task.CompletedTask);

        return new SanityController(new SanityConverter(), _contentProvider.Object, eventPublisher.Object, NullLogger<SanityController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }
}
