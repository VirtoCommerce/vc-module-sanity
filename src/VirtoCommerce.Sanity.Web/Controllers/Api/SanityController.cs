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
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Sanity.Core.Services;
using VirtoCommerce.Sanity.Data.ContentProviders;
using Permissions = VirtoCommerce.Sanity.Core.ModuleConstants.Security.Permissions;

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
    /// </summary>
    [HttpPost]
    public async Task<ActionResult> Post(
        [FromQuery] string cultureName,
        [FromHeader(Name = "sanity-operation")] string operation,
        [FromBody] JObject body)
    {
        var pageOperation = sanityConverter.GetPageOperation(operation);
        if (pageOperation == PageOperation.Unknown)
        {
            return Ok();
        }

        if ((pageOperation == PageOperation.Delete && !User.HasGlobalPermission(Permissions.Delete)) ||
            !User.HasGlobalPermission(Permissions.Update))
        {
            return Forbid();
        }

        var documentId = body?["_id"]?.ToString();
        if (string.IsNullOrEmpty(documentId))
        {
            logger.LogWarning("Sanity webhook notification carries no document id, nothing to index.");
            return Ok();
        }

        var pageDocuments = await GetPageDocumentsAsync(documentId, cultureName, pageOperation, body);

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
    /// fetched and enriched the same way as during indexing, from the Sanity sources of every store
    /// that has them configured. A deleted document can no longer be fetched, so for that operation
    /// the payload is the only available source of its id.
    /// </summary>
    private async Task<IList<PageDocument>> GetPageDocumentsAsync(
        string documentId, string cultureName, PageOperation pageOperation, JObject body)
    {
        if (pageOperation == PageOperation.Delete)
        {
            var deletedDocument = sanityConverter.GetPageDocument(null, cultureName, pageOperation, body, Request);
            return deletedDocument != null ? [deletedDocument] : [];
        }

        var pageDocuments = await sanityContentProvider.GetByIdsAsync([documentId]);

        if (pageDocuments.Count == 0)
        {
            logger.LogWarning(
                "Sanity document '{DocumentId}' from the webhook notification was not found in any configured Sanity source, nothing to index.",
                documentId);
        }

        return pageDocuments;
    }
}
