using System.Collections.Generic;
using System.Threading.Tasks;
using VirtoCommerce.Pages.Core.ContentProviders;
using VirtoCommerce.Pages.Core.Models;

namespace VirtoCommerce.Sanity.Core.Services;

/// <summary>
/// Page content provider for the Sanity sources configured in store settings.
/// </summary>
public interface ISanityContentProvider : IPageContentProvider
{
    /// <summary>
    /// Fetches documents the same way as indexing does, restricted to a single Sanity project and
    /// dataset when they are given.
    /// </summary>
    Task<IList<PageDocument>> GetByIdsAsync(IList<string> ids, string projectId, string dataset);

    /// <summary>
    /// Returns the ids of the enabled stores whose settings name the given Sanity project and dataset.
    /// </summary>
    Task<IList<string>> GetStoreIdsAsync(string projectId, string dataset);
}
