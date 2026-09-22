using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Pages.Core.Events;
using VirtoCommerce.Pages.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Sanity.Core.Services;
using VirtoCommerce.Sanity.Data.ContentProviders;
using VirtoCommerce.Sanity.Web.Filters;

namespace VirtoCommerce.Sanity.Web.Controllers.Api;

[Authorize]
[Route("api/pages/sanity")]
public class SanityController(
    ISanityConverter sanityConverter,
    SanityContentProvider sanityContentProvider,
    IEventPublisher eventPublisher,
    ILogger<SanityController> logger)
    : Controller
{
    // POST: /api/pages/sanity
    /// <summary>
    /// Create, update or delete page in Pages module based on the notification from Sanity webhook.
    /// The request is authenticated by its signature instead of an API key, so no secret has to be
    /// put in the webhook URL configured in Sanity.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [SanityWebhookSignature]
    public async Task<ActionResult> Post(
        [FromQuery] string cultureName,
        [FromHeader(Name = "sanity-operation")] string operation,
        [FromHeader(Name = "sanity-project-id")] string projectId,
        [FromHeader(Name = "sanity-dataset")] string dataset,
        [FromHeader(Name = "sanity-document-id")] string documentId,
        [FromBody] JObject body)
    {
        var pageOperation = sanityConverter.GetPageOperation(operation);
        if (pageOperation == PageOperation.Unknown)
        {
            return Ok();
        }

        // Sanity names the changed document in a header, so the payload projection may be empty
        if (string.IsNullOrEmpty(documentId))
        {
            documentId = body?["_id"]?.ToString();
        }

        if (string.IsNullOrEmpty(documentId))
        {
            logger.LogWarning("Sanity webhook notification carries no document id, nothing to index.");
            return Ok();
        }

        var pageDocuments = await GetPageDocumentsAsync(documentId, projectId, dataset, cultureName, pageOperation, body);

        foreach (var pageDocument in pageDocuments)
        {
            if (pageDocument.CultureName.IsNullOrEmpty())
            {
                pageDocument.CultureName = cultureName;
            }

            var pageChangedEvent = AbstractTypeFactory<PagesDomainEvent>.TryCreateInstance();
            pageChangedEvent.Page = pageDocument;
            pageChangedEvent.Operation = pageOperation;

            await eventPublisher.Publish(pageChangedEvent);
        }

        return Ok();
    }

    /// <summary>
    /// The webhook payload is treated as a notification, not as content: it may carry only ids or a
    /// partial projection, and it never has internal links and asset URLs resolved. So the document is
    /// fetched and enriched the same way as during indexing, from the project and dataset the
    /// notification names. A deleted document can no longer be fetched, so it is published from its
    /// id alone.
    /// </summary>
    private async Task<IList<PageDocument>> GetPageDocumentsAsync(
        string documentId, string projectId, string dataset, string cultureName, PageOperation pageOperation, JObject body)
    {
        if (pageOperation == PageOperation.Delete)
        {
            body ??= [];

            // The converter needs the id, which the header carries even when the payload is empty
            body["_id"] ??= documentId;

            var deletedDocument = sanityConverter.GetPageDocument(null, cultureName, pageOperation, body, Request);
            return deletedDocument != null ? [deletedDocument] : [];
        }

        var pageDocuments = await sanityContentProvider.GetByIdsAsync([documentId], projectId, dataset);

        if (pageDocuments.Count == 0)
        {
            logger.LogWarning(
                "Sanity document '{DocumentId}' from the webhook notification was not found in project '{ProjectId}' dataset '{Dataset}', nothing to index. Check that the project and its document type are configured for a store.",
                documentId, projectId, dataset);
        }

        return pageDocuments;
    }
}
