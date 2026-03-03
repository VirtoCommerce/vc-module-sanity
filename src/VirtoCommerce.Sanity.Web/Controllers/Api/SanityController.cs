using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions = VirtoCommerce.Sanity.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.Sanity.Web.Controllers.Api;

[Authorize]
[Route("api/sanity")]
public class SanityController : Controller
{
    // GET: api/sanity
    /// <summary>
    /// Get message
    /// </summary>
    /// <remarks>Return "Hello world!" message</remarks>
    [HttpGet]
    [Route("")]
    [Authorize(Permissions.Read)]
    public ActionResult<string> Get()
    {
        return Ok(new { result = "Hello world!" });
    }
}
