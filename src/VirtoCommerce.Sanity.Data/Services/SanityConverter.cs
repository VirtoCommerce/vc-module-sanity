using System;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Pages.Core.Events;
using VirtoCommerce.Pages.Core.Extensions;
using VirtoCommerce.Pages.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Sanity.Core.Services;

namespace VirtoCommerce.Sanity.Data.Services;

public class SanityConverter : ISanityConverter
{
    public virtual PageOperation GetPageOperation(string operation)
    {
        if (operation.EqualsIgnoreCase("create") || operation.EqualsIgnoreCase("update"))
        {
            return PageOperation.Publish;
        }

        if (operation.EqualsIgnoreCase("delete"))
        {
            return PageOperation.Delete;
        }

        return PageOperation.Unknown;
    }

    public virtual PageDocument GetPageDocument(string storeId, string cultureName, PageOperation pageOperation, JObject body, HttpRequest request)
    {
        if (body["_type"].ToString().EqualsIgnoreCase("system.release"))
        {
            return null;
        }

        var pageDocument = AbstractTypeFactory<PageDocument>.TryCreateInstance();
        pageDocument.Source = "sanity";
        pageDocument.MimeType = "application/json";
        pageDocument.Content = body.ToString();

        pageDocument.StoreId = body["storeId"]?.ToString();
        pageDocument.CultureName = body["cultureName"]?.ToString();

        pageDocument.OuterId = body["_id"].ToString();
        pageDocument.Id = pageDocument.OuterId;
        pageDocument.CreatedDate = body["_createdAt"].ToObject<DateTime>();
        pageDocument.ModifiedDate = body["_updatedAt"].ToObject<DateTime>();

        var idStartsWithDrafts = pageDocument.Id.StartsWithIgnoreCase("drafts.");

        pageDocument.Status = idStartsWithDrafts
            ? PageDocumentStatus.Draft
            : pageOperation.GetPageDocumentStatus();

        pageDocument.Permalink = body["permalink"]?["current"]?.ToString();
        pageDocument.Title = body["title"]?.ToString();
        pageDocument.Description = body["description"]?.ToString();
        pageDocument.Visibility = EnumUtility.SafeParse(body["visibility"]?.ToString(), PageDocumentVisibility.Private);
        pageDocument.UserGroups = body["userGroups"]?.ToObject<string[]>();
        pageDocument.StartDate = body["startDate"]?.ToObject<DateTime?>();
        pageDocument.EndDate = body["endDate"]?.ToObject<DateTime?>();

        return pageDocument;
    }
}
