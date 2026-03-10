using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Pages.Core.Events;
using VirtoCommerce.Pages.Core.Models;

namespace VirtoCommerce.Sanity.Core.Services;

public interface ISanityConverter
{
    PageOperation GetPageOperation(string operation);
    PageDocument GetPageDocument(string storeId, string cultureName, PageOperation pageOperation, JObject body, HttpRequest request);
}
