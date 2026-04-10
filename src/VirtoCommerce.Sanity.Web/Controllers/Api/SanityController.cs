using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Pages.Core.Events;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Sanity.Core.Services;
using Permissions = VirtoCommerce.Sanity.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.Sanity.Web.Controllers.Api;

[Authorize]
[Route("api/pages/sanity")]
public class SanityController(
    ISanityConverter sanityConverter,
    IEventPublisher eventPublisher)
    : Controller
{
    // POST: /api/pages/sanity
    /// <summary>
    /// Create, update or delete page in Pages module based on the payload from Sanity webhook.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult> Post(
        [FromQuery] string storeId,
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

        var pageDocument = sanityConverter.GetPageDocument(storeId, cultureName, pageOperation, body, Request);
        if (pageDocument != null)
        {
            if (pageDocument.StoreId.IsNullOrEmpty())
            {
                pageDocument.StoreId = storeId;
            }
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
}
