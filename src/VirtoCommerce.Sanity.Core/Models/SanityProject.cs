using System.Collections.Generic;

namespace VirtoCommerce.Sanity.Core.Models;

/// <summary>
/// One entry of the Sanity.Projects store setting, deserialized directly from the setting JSON.
/// </summary>
public class SanityProject
{
    /// <summary>
    /// Sanity project id. Required; entries without it are skipped.
    /// </summary>
    public string ProjectId { get; set; }

    /// <summary>
    /// Project API token. Optional; the store-level ApiToken setting is used when empty.
    /// </summary>
    public string ApiToken { get; set; }

    /// <summary>
    /// Datasets of the project. When empty, the single "production" dataset is used.
    /// A dataset marked with <see cref="SanityDataset.IsPriority"/> is queried first.
    /// </summary>
    public List<SanityDataset> Datasets { get; set; } = [];
}
