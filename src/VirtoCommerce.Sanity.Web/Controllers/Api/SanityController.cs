using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Pages.Core.Events;
using VirtoCommerce.Pages.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Sanity.Core;
using VirtoCommerce.Sanity.Core.Services;
using VirtoCommerce.Sanity.Web.Filters;

namespace VirtoCommerce.Sanity.Web.Controllers.Api;

[Authorize]
[Route("api/pages/sanity")]
public class SanityController(
    ISanityConverter sanityConverter,
    ISanityContentProvider sanityContentProvider,
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
        // The signature covers the body only, not the sanity-* headers. Values the webhook projection
        // puts in the body therefore take precedence, and a header contradicting them is rejected:
        // otherwise a replayed delivery could be pointed at another document or turned into a delete.
        if (!TryUseSignedValue(body, "operation", "sanity-operation", ref operation) ||
            !TryUseSignedValue(body, "_id", "sanity-document-id", ref documentId) ||
            !TryUseSignedValue(body, "projectId", "sanity-project-id", ref projectId) ||
            !TryUseSignedValue(body, "dataset", "sanity-dataset", ref dataset))
        {
            return BadRequest();
        }

        var pageOperation = sanityConverter.GetPageOperation(operation);
        if (pageOperation == PageOperation.Unknown)
        {
            return Ok();
        }

        if (string.IsNullOrEmpty(documentId))
        {
            logger.LogWarning("Sanity webhook notification carries no document id, nothing to index.");
            return Ok();
        }

        if (!SanityIdentifier.IsValid(documentId))
        {
            logger.LogWarning("Sanity webhook notification rejected: '{DocumentId}' is not a valid Sanity document id.", documentId);
            return BadRequest();
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
    /// id alone, once for every store configured for its source unless the payload names the store.
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
            if (deletedDocument == null || !deletedDocument.StoreId.IsNullOrEmpty())
            {
                return deletedDocument != null ? [deletedDocument] : [];
            }

            // The payload names no store; handlers of the delete event still get the store it belonged to
            var storeIds = await sanityContentProvider.GetStoreIdsAsync(projectId, dataset);
            if (storeIds.Count == 0)
            {
                return [deletedDocument];
            }

            return storeIds
                .Select(storeId =>
                {
                    var storeDocument = sanityConverter.GetPageDocument(storeId, cultureName, pageOperation, body, Request);
                    storeDocument.StoreId = storeId;
                    return storeDocument;
                })
                .ToList();
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

    /// <summary>
    /// Replaces a header value with the same value from the signed body when the body carries it.
    /// Returns false when both are present and differ.
    /// </summary>
    private bool TryUseSignedValue(JObject body, string bodyPropertyName, string headerName, ref string value)
    {
        var signedValue = body?[bodyPropertyName]?.ToString();
        if (string.IsNullOrEmpty(signedValue))
        {
            return true;
        }

        // Ordinal: Sanity document ids are case-sensitive
        if (!string.IsNullOrEmpty(value) && !string.Equals(value, signedValue, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Sanity webhook notification rejected: the '{HeaderName}' header '{HeaderValue}' does not match '{PropertyName}' '{SignedValue}' in the signed payload.",
                headerName, value, bodyPropertyName, signedValue);
            return false;
        }

        value = signedValue;
        return true;
    }
}
